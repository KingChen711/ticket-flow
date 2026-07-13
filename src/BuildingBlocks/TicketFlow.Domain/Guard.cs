namespace TicketFlow.Domain;

public static class Guard
{
    public static string NotNullOrEmpty(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or empty.", paramName);

        return value;
    }

    public static decimal NotNegative(decimal value, string paramName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Value cannot be negative.");

        return value;
    }

    public static int NotNegative(int value, string paramName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Value cannot be negative.");

        return value;
    }

    public static void Range(DateTimeOffset start, DateTimeOffset end, string paramName)
    {
        if (end <= start)
            throw new ArgumentOutOfRangeException(paramName, end, "End must be after start.");
    }
}
