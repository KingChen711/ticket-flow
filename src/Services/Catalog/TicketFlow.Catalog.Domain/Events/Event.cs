using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed class Event : AggregateRoot<Guid>
{
    private readonly List<PriceTier> _priceTiers = [];

    public string Title { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public string Artist { get; private set; } = null!;
    public Guid VenueId { get; private set; }
    public Guid? OrganizerId { get; private set; }
    public long AggregateVersion { get; private set; }
    public long PriceSnapshotVersion { get; private set; }
    public EventSchedule Schedule { get; private set; } = null!;
    public EventStatus Status { get; private set; }
    public IReadOnlyList<PriceTier> PriceTiers => _priceTiers.AsReadOnly();

    // Required by EF Core: Schedule is an owned navigation, can't be bound through a
    // constructor parameter — EF materializes via this parameterless constructor, then
    // sets every property (including Schedule and the PriceTiers backing field) via reflection.
    private Event() { }

    private Event(Guid id, string title, string description, string artist, Guid venueId, EventSchedule schedule)
        : base(id)
    {
        Title = title;
        Description = description;
        Artist = artist;
        VenueId = venueId;
        Schedule = schedule;
        Status = EventStatus.Draft;
        AggregateVersion = 1;
    }

    public static Event Create(string title, string description, string artist, Guid venueId, EventSchedule schedule)
    {
        var @event = new Event(
            Guid.NewGuid(),
            Guard.NotNullOrEmpty(title, nameof(title)),
            description ?? string.Empty,
            Guard.NotNullOrEmpty(artist, nameof(artist)),
            venueId,
            schedule ?? throw new ArgumentNullException(nameof(schedule)));

        @event.Raise(new EventCreated(@event.Id, @event.Title, @event.VenueId));

        return @event;
    }

    /// <summary>
    /// Only enforces invariants local to this aggregate (draft-only, no duplicate section).
    /// Whether <paramref name="sectionId"/> actually exists in this event's SeatMap is a
    /// cross-aggregate rule, validated by the application layer before calling this method.
    /// </summary>
    public void AddPriceTier(Guid sectionId, Money price)
    {
        if (Status != EventStatus.Draft)
            throw new InvalidOperationException("Price tiers can only be added while the event is in draft.");

        if (_priceTiers.Any(p => p.SectionId == sectionId))
            throw new InvalidOperationException($"A price tier for section '{sectionId}' already exists.");

        _priceTiers.Add(PriceTier.Create(sectionId, price));
        AggregateVersion++;
        PriceSnapshotVersion++;
    }

    public void Publish(DateTimeOffset now)
    {
        if (Status != EventStatus.Draft)
            throw new InvalidOperationException("Only a draft event can be published.");

        if (_priceTiers.Count == 0)
            throw new InvalidOperationException("Cannot publish an event without at least one price tier.");

        if (Schedule.IsInPast(now))
            throw new InvalidOperationException("Cannot publish an event scheduled in the past.");

        if (_priceTiers.Select(tier => tier.Price.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new InvalidOperationException("All price tiers for an event must use one currency.");

        Status = EventStatus.Published;
        AggregateVersion++;
        PriceSnapshotVersion++;
        Raise(new EventPublished(Id));
    }

    public void Reschedule(EventSchedule schedule, DateTimeOffset now)
    {
        if (Status == EventStatus.Cancelled)
            throw new InvalidOperationException("Cannot reschedule a cancelled event.");

        if (schedule is null)
            throw new ArgumentNullException(nameof(schedule));

        if (schedule.IsInPast(now))
            throw new InvalidOperationException("Cannot reschedule an event to a time in the past.");

        Schedule = schedule;
        AggregateVersion++;
        PriceSnapshotVersion++;
        Raise(new EventRescheduled(Id, Schedule.StartsAt, Schedule.EndsAt));
    }

    public void Cancel()
    {
        if (Status == EventStatus.Cancelled)
            throw new InvalidOperationException("Event is already cancelled.");

        Status = EventStatus.Cancelled;
        AggregateVersion++;
        PriceSnapshotVersion++;
        Raise(new EventCancelled(Id));
    }
}
