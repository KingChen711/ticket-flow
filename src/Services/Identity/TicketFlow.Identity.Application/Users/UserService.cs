using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Application.Users;

public sealed class UserService(
    IUserRepository userRepository,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork)
{
    public async Task<UserResponse> RegisterAsync(RegisterUserRequest request, CancellationToken cancellationToken = default)
    {
        var email = Email.Create(request.Email);

        if (await userRepository.GetByEmailAsync(email.Value, cancellationToken) is not null)
            throw new InvalidOperationException($"Email '{email.Value}' is already registered.");

        var passwordHash = passwordHasher.Hash(request.Password);
        var user = User.Register(email, request.DisplayName, passwordHash);

        userRepository.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(user);
    }

    /// <summary>Returns null for both "no such user" and "wrong password" — never reveal which.</summary>
    public async Task<UserResponse?> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken = default)
    {
        var email = Email.Create(request.Email);
        var user = await userRepository.GetByEmailAsync(email.Value, cancellationToken);

        if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHash))
            return null;

        return Map(user);
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
