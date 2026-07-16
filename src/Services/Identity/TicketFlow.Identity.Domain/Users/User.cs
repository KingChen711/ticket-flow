using TicketFlow.Domain;

namespace TicketFlow.Identity.Domain.Users;

public sealed class User : AggregateRoot<Guid>
{
    private readonly List<Role> _roles = [];

    public Email Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public IReadOnlyList<Role> Roles => _roles.AsReadOnly();

    // Required by EF Core: Email is an owned navigation, can't be bound through a
    // constructor parameter — see Catalog's Event/Venue for the same reason.
    private User() { }

    private User(Guid id, Email email, string displayName, Role role) : base(id)
    {
        Email = email;
        DisplayName = displayName;
        _roles.Add(role);
    }

    // Phase 7: Keycloak is authN authority and assigns the real user id — it does not honor a
    // client-supplied one, so id must be the value Keycloak returned, not a locally-minted Guid
    // (see IKeycloakAdminClient.CreateUserAsync and docs/phase-7-plan.md Commit 3).
    public static User Register(Guid id, Email email, string displayName, Role role = Role.Customer)
    {
        var user = new User(
            id,
            email ?? throw new ArgumentNullException(nameof(email)),
            Guard.NotNullOrEmpty(displayName, nameof(displayName)),
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
