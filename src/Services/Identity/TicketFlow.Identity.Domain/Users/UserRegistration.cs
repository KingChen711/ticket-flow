using TicketFlow.Domain;

namespace TicketFlow.Identity.Domain.Users;

public enum UserRegistrationStatus
{
    Pending,
    Completed,
    CompensationPending,
}

/// <summary>
/// Durable coordinator for the Keycloak + local-profile registration workflow. The password is
/// deliberately never persisted; a retry must present it again under the same registration key.
/// </summary>
public sealed class UserRegistration : Entity<Guid>
{
    public string NormalizedEmail { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public UserRegistrationStatus Status { get; private set; }
    public Guid? KeycloakUserId { get; private set; }
    public DateTimeOffset? CreateAttemptedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string? LastError { get; private set; }

    private UserRegistration() { }

    private UserRegistration(Guid registrationKey, string normalizedEmail, string displayName, DateTimeOffset now)
        : base(registrationKey)
    {
        if (registrationKey == Guid.Empty)
            throw new ArgumentException("RegistrationKey must not be empty.", nameof(registrationKey));

        NormalizedEmail = Guard.NotNullOrEmpty(normalizedEmail, nameof(normalizedEmail));
        DisplayName = Guard.NotNullOrEmpty(
            Guard.NotNullOrEmpty(displayName, nameof(displayName)).Trim(),
            nameof(displayName));
        Status = UserRegistrationStatus.Pending;
        UpdatedAt = now;
    }

    public static UserRegistration Start(
        Guid registrationKey,
        string normalizedEmail,
        string displayName,
        DateTimeOffset now) =>
        new(registrationKey, normalizedEmail, displayName, now);

    public void EnsureSameIntent(string normalizedEmail, string displayName)
    {
        var normalizedDisplayName = Guard.NotNullOrEmpty(
            Guard.NotNullOrEmpty(displayName, nameof(displayName)).Trim(),
            nameof(displayName));
        if (!string.Equals(NormalizedEmail, normalizedEmail, StringComparison.Ordinal) ||
            !string.Equals(DisplayName, normalizedDisplayName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Registration key '{Id}' was already used for a different registration intent.");
        }
    }

    public void MarkCreateAttempted(DateTimeOffset now)
    {
        if (Status == UserRegistrationStatus.Completed)
            return;

        CreateAttemptedAt ??= now;
        Status = UserRegistrationStatus.Pending;
        UpdatedAt = now;
        LastError = null;
    }

    public void RecordKeycloakUser(Guid userId, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("Keycloak user id must not be empty.", nameof(userId));

        if (KeycloakUserId is not null && KeycloakUserId != userId)
            throw new InvalidOperationException("Registration is already linked to another Keycloak user.");

        KeycloakUserId = userId;
        Status = UserRegistrationStatus.Pending;
        UpdatedAt = now;
        LastError = null;
    }

    public void Complete(DateTimeOffset now)
    {
        if (KeycloakUserId is null)
            throw new InvalidOperationException("A registration cannot complete without a Keycloak user.");

        Status = UserRegistrationStatus.Completed;
        CompletedAt ??= now;
        UpdatedAt = now;
        LastError = null;
    }

    public void ResetAfterCompensation(DateTimeOffset now)
    {
        KeycloakUserId = null;
        CreateAttemptedAt = null;
        Status = UserRegistrationStatus.Pending;
        CompletedAt = null;
        UpdatedAt = now;
        LastError = null;
    }

    public void MarkCompensationPending(string error, DateTimeOffset now)
    {
        Status = UserRegistrationStatus.CompensationPending;
        UpdatedAt = now;
        LastError = Guard.NotNullOrEmpty(error, nameof(error));
    }
}
