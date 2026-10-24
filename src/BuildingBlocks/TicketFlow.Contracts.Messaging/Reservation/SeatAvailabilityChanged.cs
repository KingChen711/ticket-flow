namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Durable notification intent emitted after a reservation allocation changes. The exact seat
/// set and generation let real-time clients ignore duplicate or out-of-order SignalR deliveries.
/// SignalR itself remains a non-critical projection of this event.
/// </summary>
public sealed record SeatAvailabilityChanged(
    Guid EventId,
    IReadOnlyList<Guid> SeatIds,
    string Status,
    long Generation,
    DateTimeOffset OccurredAt);
