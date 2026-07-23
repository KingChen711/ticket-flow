using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Application.Abstractions;

// Keycloak is the authority for authN. The operations are deliberately split so UserService can
// durably checkpoint and recover each boundary instead of hiding a multi-step remote workflow
// inside one opaque call.
public interface IKeycloakAdminClient
{
    Task<Guid?> FindUserByNormalizedEmailAsync(
        string normalizedEmail,
        Guid registrationKey,
        CancellationToken cancellationToken = default);

    Task<Guid> CreateUserAsync(
        string normalizedEmail,
        string displayName,
        Guid registrationKey,
        CancellationToken cancellationToken = default);

    Task ConfigureUserAsync(
        Guid keycloakUserId,
        string password,
        Role role,
        CancellationToken cancellationToken = default);

    Task DeleteUserAsync(Guid keycloakUserId, CancellationToken cancellationToken = default);
}

public sealed class KeycloakUserAlreadyExistsException(string normalizedEmail)
    : InvalidOperationException($"A Keycloak user already exists for '{normalizedEmail}'.");
