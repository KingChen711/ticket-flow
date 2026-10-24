namespace TicketFlow.Identity.Application.Users;

public static class RegistrationPasswordPolicy
{
    public static void Validate(string password, string normalizedEmail)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new ArgumentException("Password must contain at least 12 characters.", nameof(password));

        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) ||
            !password.Any(char.IsDigit) || !password.Any(character => !char.IsLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "Password must contain uppercase, lowercase, numeric, and special characters.",
                nameof(password));
        }

        var username = normalizedEmail.Split('@', 2)[0];
        if (username.Length >= 3 && password.Contains(username, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Password must not contain the email username.", nameof(password));
    }
}
