using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Application.Users;

public sealed class UserService(
    IUserRepository userRepository,
    IKeycloakAdminClient keycloakAdminClient,
    IUnitOfWork unitOfWork)
{
    // Dual-write, Keycloak first: Keycloak owns authN (Phase 7) and assigns the real user id
    // (it ignores a client-supplied one), so the local profile row has to be created SECOND,
    // using that id. If the local save fails, the Keycloak user is deleted to compensate —
    // minimal rollback, not a distributed transaction (see docs/phase-7-plan.md Commit 3).
    public async Task<UserResponse> RegisterAsync(RegisterUserRequest request, CancellationToken cancellationToken = default)
    {
        var email = Email.Create(request.Email);

        if (await userRepository.GetByEmailAsync(email.Value, cancellationToken) is not null)
            throw new InvalidOperationException($"Email '{email.Value}' is already registered.");

        var keycloakUserId = await keycloakAdminClient.CreateUserAsync(
            email.Value, request.DisplayName, request.Password, Role.Customer, cancellationToken);

        try
        {
            var user = User.Register(keycloakUserId, email, request.DisplayName);

            userRepository.Add(user);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Map(user);
        }
        catch
        {
            await keycloakAdminClient.DeleteUserAsync(keycloakUserId, cancellationToken);
            throw;
        }
    }

    public async Task<UserResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(id, cancellationToken);
        return user is null ? null : Map(user);
    }

    public async Task<IReadOnlyList<UserResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var users = await userRepository.ListAsync(cancellationToken);
        return users.Select(Map).ToList();
    }

    private static UserResponse Map(User user) => new(
        user.Id,
        user.Email.Value,
        user.DisplayName,
        user.Roles.Select(role => role.ToString()).ToList());
}
