using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Infrastructure.Keycloak;

/// <summary>
/// Thin, step-oriented Keycloak Admin API client. UserService persists a checkpoint between these
/// calls and owns recovery/compensation; this client never tries to hide a distributed workflow.
/// </summary>
public sealed class KeycloakAdminClient(HttpClient httpClient, KeycloakAdminOptions options) : IKeycloakAdminClient
{
    private const string RegistrationKeyAttribute = "ticketflowRegistrationKey";

    public async Task<Guid?> FindUserByNormalizedEmailAsync(
        string normalizedEmail,
        Guid registrationKey,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        var byUsername = await FindUsersAsync("username", normalizedEmail, token, cancellationToken);
        var byEmail = await FindUsersAsync("email", normalizedEmail, token, cancellationToken);

        var exactMatches = byUsername.Concat(byEmail)
            .Where(user =>
                (string.Equals(user.Username, normalizedEmail, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase)) &&
                HasRegistrationKey(user, registrationKey))
            .DistinctBy(user => user.Id)
            .ToList();

        return exactMatches.Count switch
        {
            0 => null,
            1 => exactMatches[0].Id,
            _ => throw new InvalidOperationException(
                $"Keycloak returned multiple exact users for normalized email '{normalizedEmail}'."),
        };
    }

    public async Task<Guid> CreateUserAsync(
        string normalizedEmail,
        string displayName,
        Guid registrationKey,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"/admin/realms/{options.Realm}/users");
        createRequest.Content = JsonContent.Create(new
        {
            username = normalizedEmail,
            email = normalizedEmail,
            firstName = displayName,
            enabled = true,
            emailVerified = false,
            requiredActions = new[] { "VERIFY_EMAIL" },
            attributes = new Dictionary<string, string[]>
            {
                [RegistrationKeyAttribute] = [registrationKey.ToString("D")],
            },
        });
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var createResponse = await httpClient.SendAsync(createRequest, cancellationToken);
        if (createResponse.StatusCode == HttpStatusCode.Conflict)
            throw new KeycloakUserAlreadyExistsException(normalizedEmail);

        createResponse.EnsureSuccessStatusCode();

        var location = createResponse.Headers.Location
                       ?? throw new InvalidOperationException(
                           "Keycloak did not return a Location header for the created user.");
        return Guid.Parse(location.Segments[^1]);
    }

    public async Task ConfigureUserAsync(
        Guid keycloakUserId,
        string password,
        Role role,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        await SetPasswordAsync(keycloakUserId, password, token, cancellationToken);
        await AssignRoleAsync(keycloakUserId, role, token, cancellationToken);
    }

    public async Task DeleteUserAsync(Guid keycloakUserId, CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        using var request =
            new HttpRequestMessage(HttpMethod.Delete, $"/admin/realms/{options.Realm}/users/{keycloakUserId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        // Compensation is idempotent: an already-absent remote user is the desired outcome.
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    private async Task<IReadOnlyList<KeycloakUserRepresentation>> FindUsersAsync(
        string field,
        string value,
        string token,
        CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(value);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/realms/{options.Realm}/users?{field}={encoded}&exact=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<KeycloakUserRepresentation>>(cancellationToken)
               ?? [];
    }

    private async Task SetPasswordAsync(
        Guid userId,
        string password,
        string token,
        CancellationToken cancellationToken)
    {
        using var request =
            new HttpRequestMessage(HttpMethod.Put, $"/admin/realms/{options.Realm}/users/{userId}/reset-password");
        request.Content = JsonContent.Create(new { type = "password", value = password, temporary = false });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task AssignRoleAsync(
        Guid userId,
        Role role,
        string token,
        CancellationToken cancellationToken)
    {
        var roleName = role.ToString().ToLowerInvariant();

        using var roleRequest =
            new HttpRequestMessage(HttpMethod.Get, $"/admin/realms/{options.Realm}/roles/{roleName}");
        roleRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var roleResponse = await httpClient.SendAsync(roleRequest, cancellationToken);
        roleResponse.EnsureSuccessStatusCode();
        var roleRepresentation = await roleResponse.Content.ReadAsStringAsync(cancellationToken);

        using var assignRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/realms/{options.Realm}/users/{userId}/role-mappings/realm");
        assignRequest.Content = new StringContent($"[{roleRepresentation}]", Encoding.UTF8, "application/json");
        assignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var assignResponse = await httpClient.SendAsync(assignRequest, cancellationToken);
        assignResponse.EnsureSuccessStatusCode();
    }

    private async Task<string> GetAdminTokenAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"/realms/{options.Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.AdminClientId,
                ["client_secret"] = options.AdminClientSecret,
            }),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return payload?.AccessToken
               ?? throw new InvalidOperationException("Keycloak token response did not contain access_token.");
    }

    private static bool HasRegistrationKey(KeycloakUserRepresentation user, Guid registrationKey) =>
        user.Attributes is not null &&
        user.Attributes.TryGetValue(RegistrationKeyAttribute, out var values) &&
        values.Contains(registrationKey.ToString("D"), StringComparer.OrdinalIgnoreCase);

    private sealed record KeycloakUserRepresentation(
        Guid Id,
        string? Username,
        string? Email,
        Dictionary<string, string[]>? Attributes);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")]
        string AccessToken);
}
