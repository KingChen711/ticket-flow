using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow.Web;

/// <summary>
/// Supplies the client-credentials access token that one TicketFlow service presents to another
/// (Phase 12 Commit 14), so the callee can enforce <c>RequireServicePrincipal</c> instead of
/// trusting that only our own containers can reach its port.
/// </summary>
public interface IServiceTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-wide token cache. Registered as a singleton on purpose: the token belongs to the
/// service, not to any one outbound client, and a per-handler cache would re-fetch once per
/// HttpClient and again for every gRPC channel.
/// </summary>
/// <remarks>
/// Refresh is driven by the token's own <c>expires_in</c> minus a safety margin rather than a
/// fixed interval, so a realm that shortens token lifetime does not silently start handing out
/// tokens that expire in flight.
/// </remarks>
public sealed class ServiceTokenProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : IServiceTokenProvider, IDisposable
{
    internal const string TokenClientName = "ticketflow-service-token";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _token;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-checked inside the gate: many callers can queue behind one expiry, and only the
            // first of them should actually go to Keycloak.
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _token;

            var section = configuration.GetSection("Keycloak");
            var tokenEndpoint = section["TokenEndpoint"]
                ?? throw new InvalidOperationException("Missing configuration: Keycloak:TokenEndpoint");
            var clientId = section["ServiceClientId"]
                ?? throw new InvalidOperationException("Missing configuration: Keycloak:ServiceClientId");
            var clientSecret = section["ServiceClientSecret"]
                ?? throw new InvalidOperationException("Missing configuration: Keycloak:ServiceClientSecret");

            var client = httpClientFactory.CreateClient(TokenClientName);
            using var response = await client.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                }),
                cancellationToken);

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Keycloak returned an empty token response.");

            _token = payload.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn) - RefreshMargin;
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>Attaches the service token to outbound HTTP calls.</summary>
public sealed class ServiceTokenHandler(IServiceTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await tokens.GetTokenAsync(cancellationToken));

        return await base.SendAsync(request, cancellationToken);
    }
}

public static class ServiceTokenExtensions
{
    /// <summary>
    /// Registers the token provider, the HTTP handler, and the bare client used to reach Keycloak.
    /// That client is registered by name and left plain on purpose — routing the token request
    /// through a handler that itself needs a token is infinite recursion, not a config detail.
    /// </summary>
    public static IServiceCollection AddServiceTokenHandler(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Keycloak");
        var tokenEndpoint = section["TokenEndpoint"];
        var clientId = section["ServiceClientId"];
        var clientSecret = section["ServiceClientSecret"];
        if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var tokenEndpointUri))
            throw new InvalidOperationException(
                "Missing or invalid configuration: Keycloak:TokenEndpoint must be an absolute URI.");
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Missing configuration: Keycloak:ServiceClientId");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Missing configuration: Keycloak:ServiceClientSecret");

        var production = string.Equals(
            configuration["ASPNETCORE_ENVIRONMENT"], "Production", StringComparison.OrdinalIgnoreCase);
        if (production && tokenEndpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Production Keycloak service-token endpoint must use HTTPS.");
        }

        services.AddHttpClient(ServiceTokenProvider.TokenClientName);
        services.AddSingleton<IServiceTokenProvider, ServiceTokenProvider>();
        services.AddTransient<ServiceTokenHandler>();
        return services;
    }
}
