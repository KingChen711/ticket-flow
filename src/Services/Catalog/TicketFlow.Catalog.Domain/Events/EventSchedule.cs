using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed class EventSchedule : ValueObject
{
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; }

    private EventSchedule(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    public static EventSchedule Create(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        Guard.Range(startsAt, endsAt, nameof(endsAt));
        return new EventSchedule(startsAt, endsAt);
    }

    public bool IsInPast(DateTimeOffset now) => StartsAt <= now;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return StartsAt;
        yield return EndsAt;
    }
}
