using System.Text.RegularExpressions;
using TicketFlow.Domain;

namespace TicketFlow.Identity.Domain.Users;

public sealed partial class Email : ValueObject
{
    public string Value { get; }

    private Email(string value) => Value = value;

    public static Email Create(string value)
    {
        Guard.NotNullOrEmpty(value, nameof(value));

        var normalized = value.Trim().ToLowerInvariant();

        if (!EmailPattern().IsMatch(normalized))
            throw new ArgumentException($"'{value}' is not a valid email address.", nameof(value));

        return new Email(normalized);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
