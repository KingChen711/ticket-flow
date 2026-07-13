using TicketFlow.Domain;

namespace TicketFlow.Reservation.Domain.Seats;

/// <summary>
/// Intentionally thin (see docs/phase-2-plan.md): this entity carries only the durable truth
/// — Available vs Sold. "Held" is ephemeral, lives in Redis with a TTL, and is never persisted
/// here. Concurrency / anti-oversell is enforced by Redis atomicity in the Infrastructure hold
/// store, NOT by loading this entity and mutating it. So its only real behavior is the durable
/// Available → Sold transition on confirm.
/// </summary>
public sealed class Seat : Entity<Guid>
{
    public Guid EventId { get; private set; }
    public Guid SectionId { get; private set; }
    public string Label { get; private set; } = null!;
    public SeatStatus Status { get; private set; }

    // Required by EF Core.
    private Seat() { }

    private Seat(Guid id, Guid eventId, Guid sectionId, string label) : base(id)
    {
        EventId = eventId;
        SectionId = sectionId;
        Label = label;
        Status = SeatStatus.Available;
    }

    public static Seat Create(Guid eventId, Guid sectionId, string label) =>
        new(
            Guid.NewGuid(),
            eventId == Guid.Empty ? throw new ArgumentException("Event id cannot be empty.", nameof(eventId)) : eventId,
            sectionId == Guid.Empty ? throw new ArgumentException("Section id cannot be empty.", nameof(sectionId)) : sectionId,
            Guard.NotNullOrEmpty(label, nameof(label)));

    /// <summary>
    /// Durable Available → Sold transition, called when a hold is confirmed. Held is not a
    /// persisted state, so it is not represented here — the caller already proved exclusive
    /// ownership of the seat via its Redis hold before reaching this point.
    /// </summary>
    public void MarkSold()
    {
        if (Status == SeatStatus.Sold)
            throw new InvalidOperationException($"Seat '{Label}' is already sold.");

        Status = SeatStatus.Sold;
    }
}
