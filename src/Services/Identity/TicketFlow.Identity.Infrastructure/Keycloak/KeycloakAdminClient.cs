using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Infrastructure.Keycloak;

// Talks to Keycloak's Admin REST API via the ticketflow-identity-admin service account
// (client-credentials). Two real findings from probing this API before writing this class
// (docs/phase-7-plan.md Commit 3):
//   1. POST /users ignores a client-supplied "id" — Keycloak always assigns its own UUID,
//      returned only via the response's Location header. The caller (UserService) must use
//      THAT id for the local User row, not a pre-minted Guid.
//   2. A "credentials" array inside the POST /users body is silently ignored by this endpoint
//      (unlike realm-export.json's declarative import, which DOES honor it) — the password has
//      to be set via a separate PUT .../reset-password call, or the account can be created (201)
//      but is permanently unable to log in ("Account is not fully set up", no clear error at
//      creation time).
// No admin-token caching — register isn't a hot path, so a fresh client-credentials token per
// call keeps this simple; revisit if identity's admin API traffic ever grows.
public sealed class KeycloakAdminClient(HttpClient httpClient, KeycloakAdminOptions options) : IKeycloakAdminClient
{
    public async Task<Guid> CreateUserAsync(string email, string displayName, string password, Role role,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"/admin/realms/{options.Realm}/users");
        createRequest.Content = JsonContent.Create(new
        {
            username = email,
            email,
            firstName = displayName,
            enabled = true,
            emailVerified = true,
        });
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var createResponse = await httpClient.SendAsync(createRequest, cancellationToken);
        createResponse.EnsureSuccessStatusCode();

        var location = createResponse.Headers.Location
                       ?? throw new InvalidOperationException(
                           "Keycloak did not return a Location header for the created user.");
        var userId = Guid.Parse(location.Segments[^1]);

        try
        {
            await SetPasswordAsync(userId, password, token, cancellationToken);
            await AssignRoleAsync(userId, role, token, cancellationToken);
        }
        catch
        {
            await DeleteUserAsync(userId, cancellationToken);
            throw;
        }

        return userId;
    }

    public async Task DeleteUserAsync(Guid keycloakUserId, CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        using var request =
            new HttpRequestMessage(HttpMethod.Delete, $"/admin/realms/{options.Realm}/users/{keycloakUserId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SetPasswordAsync(Guid userId, string password, string token, CancellationToken cancellationToken)
    {
        using var request =
            new HttpRequestMessage(HttpMethod.Put, $"/admin/realms/{options.Realm}/users/{userId}/reset-password");
        request.Content = JsonContent.Create(new { type = "password", value = password, temporary = false });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task AssignRoleAsync(Guid userId, Role role, string token, CancellationToken cancellationToken)
    {
        var roleName = role.ToString().ToLowerInvariant();

        using var roleRequest =
            new HttpRequestMessage(HttpMethod.Get, $"/admin/realms/{options.Realm}/roles/{roleName}");
        roleRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var roleResponse = await httpClient.SendAsync(roleRequest, cancellationToken);
        roleResponse.EnsureSuccessStatusCode();
        var roleRepresentation = await roleResponse.Content.ReadAsStringAsync(cancellationToken);

        using var assignRequest = new HttpRequestMessage(HttpMethod.Post,
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
        return payload!.AccessToken;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")]
        string AccessToken);
}
