using System.Security.Cryptography;
using TicketFlow.Identity.Application.Abstractions;

namespace TicketFlow.Identity.Infrastructure.Security;

/// <summary>
/// PBKDF2 via the BCL (System.Security.Cryptography) — no external package needed, unlike
/// Microsoft.AspNetCore.Identity's PasswordHasher, which would drag in the whole Identity
/// framework just for this one class (see docs/phase-1-plan.md).
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string Hash(string plainTextPassword)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(plainTextPassword, salt, Iterations, Algorithm, HashSize);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string plainTextPassword, string passwordHash)
    {
        var parts = passwordHash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
            return false;

        var salt = Convert.FromBase64String(parts[1]);
        var expectedHash = Convert.FromBase64String(parts[2]);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(plainTextPassword, salt, iterations, Algorithm, expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
