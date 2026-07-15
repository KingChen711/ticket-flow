namespace TicketFlow.Ordering.Domain.Orders;

/// <summary>
/// Order-stream compensation milestone (Commit 9) appended when the reservation participant
/// releases the seat hold, on either compensation path (payment failed, or ticketing failed
/// after refund). "Order" prefix for the same naming-collision reason as OrderSeatsConfirmed —
/// TicketFlow.Contracts.Messaging.Reservation.SeatsReleased already exists.
/// </summary>
public sealed record OrderSeatsReleased(Guid OrderId, DateTimeOffset ReleasedAt);
