namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Event published by the reservation consumer after a compensation <see cref="ReleaseSeats"/>
/// command succeeds. The PurchaseSaga uses this to advance from <c>Compensating_Seats</c>
/// to <c>Cancelled</c>.
/// </summary>
public sealed record SeatsReleased(
    Guid OrderId,
    Guid HoldId,
    DateTimeOffset ReleasedAt);
