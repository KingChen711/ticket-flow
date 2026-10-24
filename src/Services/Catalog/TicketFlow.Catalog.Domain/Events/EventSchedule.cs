using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed class EventSchedule : ValueObject
{
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; }
    public string TimeZoneId { get; }

    private EventSchedule(DateTimeOffset startsAt, DateTimeOffset endsAt, string timeZoneId)
    {
        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt.ToUniversalTime();
        TimeZoneId = timeZoneId;
    }

    public static EventSchedule Create(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string? timeZoneId = "UTC",
        int? startUtcOffsetMinutes = null,
        int? endUtcOffsetMinutes = null)
    {
        Guard.Range(startsAt, endsAt, nameof(endsAt));
        var normalizedTimeZoneId = NormalizeTimeZoneId(timeZoneId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(normalizedTimeZoneId);
        ValidateOffset(startsAt, startUtcOffsetMinutes, zone, nameof(startUtcOffsetMinutes));
        ValidateOffset(endsAt, endUtcOffsetMinutes, zone, nameof(endUtcOffsetMinutes));
        return new EventSchedule(startsAt, endsAt, normalizedTimeZoneId);
    }

    public bool IsInPast(DateTimeOffset now) => StartsAt <= now;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return StartsAt;
        yield return EndsAt;
        yield return TimeZoneId;
    }

    private static string NormalizeTimeZoneId(string? timeZoneId)
    {
        var normalized = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(normalized).Id;
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException($"Time zone '{normalized}' was not found.", nameof(timeZoneId), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException($"Time zone '{normalized}' is invalid.", nameof(timeZoneId), exception);
        }
    }

    private static void ValidateOffset(
        DateTimeOffset instant,
        int? expectedOffsetMinutes,
        TimeZoneInfo zone,
        string parameterName)
    {
        if (!expectedOffsetMinutes.HasValue)
            return;

        var actual = (int)zone.GetUtcOffset(instant.UtcDateTime).TotalMinutes;
        if (actual != expectedOffsetMinutes.Value)
        {
            throw new ArgumentException(
                $"UTC offset '{expectedOffsetMinutes}' does not match time zone '{zone.Id}' at '{instant:O}' (expected '{actual}').",
                parameterName);
        }
    }
}
