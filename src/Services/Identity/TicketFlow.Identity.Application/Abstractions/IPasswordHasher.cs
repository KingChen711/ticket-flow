namespace TicketFlow.Identity.Application.Abstractions;

/// <summary>
/// Port only in this commit — concrete implementation (PBKDF2 via the BCL, no external
/// package) lands in Commit 12 alongside the registration endpoint that needs it.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string plainTextPassword);
    bool Verify(string plainTextPassword, string passwordHash);
}
