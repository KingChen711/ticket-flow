using TicketFlow.Domain;

namespace TicketFlow.Identity.Domain.Users;

public sealed class User : AggregateRoot<Guid>
{
    private readonly List<Role> _roles = [];

    public Email Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public IReadOnlyList<Role> Roles => _roles.AsReadOnly();

    // Required by EF Core: Email is an owned navigation, can't be bound through a
    // constructor parameter — see Catalog's Event/Venue for the same reason.
    private User() { }

    private User(Guid id, Email email, string displayName, string passwordHash, Role role) : base(id)
    {
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        _roles.Add(role);
    }

    public static User Register(Email email, string displayName, string passwordHash, Role role = Role.Customer)
    {
        var user = new User(
            Guid.NewGuid(),
            email ?? throw new ArgumentNullException(nameof(email)),
            Guard.NotNullOrEmpty(displayName, nameof(displayName)),
            Guard.NotNullOrEmpty(passwordHash, nameof(passwordHash)),
            role);

        user.Raise(new UserRegistered(user.Id, user.Email.Value, role));

        return user;
    }

    public void GrantRole(Role role)
    {
        if (_roles.Contains(role))
            throw new InvalidOperationException($"User already has role '{role}'.");

        _roles.Add(role);
    }
}
