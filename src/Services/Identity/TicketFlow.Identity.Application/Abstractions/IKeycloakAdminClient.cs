using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Application.Abstractions;

// Keycloak is the authority for authN (Phase 7). identity-service dual-writes on register:
// this port creates/removes the Keycloak-side account so UserService can compensate if the
// local profile save fails after the Keycloak user was already created.
public interface IKeycloakAdminClient
{
    // Returns the user id Keycloak itself assigns — it does NOT honor a client-supplied id
    // (confirmed by probing the real Admin API before writing this, see docs/phase-7-plan.md
    // Commit 3), so the local User row must be created with THIS id, not a pre-minted Guid.
    Task<Guid> CreateUserAsync(string email, string displayName, string password, Role role, CancellationToken cancellationToken = default);

    Task DeleteUserAsync(Guid keycloakUserId, CancellationToken cancellationToken = default);
}
