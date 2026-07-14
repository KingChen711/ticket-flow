namespace TicketFlow.Ordering.Domain.Orders;

/// <summary>
/// Order-stream milestone appended when the reservation participant confirms the seat hold
/// (saga: AwaitingSeats -> AwaitingPayment). Named with the "Order" prefix — not just
/// "SeatsConfirmed" — to avoid colliding with the identically-named integration event
/// TicketFlow.Contracts.Messaging.Reservation.SeatsConfirmed the saga/consumer both handle in
/// the same file scope. Same class of naming hazard as the Commit 2 Payment/Payment bug and the
/// Commit 4 Event&lt;T&gt; property naming.
/// </summary>
public sealed record OrderSeatsConfirmed(Guid OrderId, DateTimeOffset ConfirmedAt);
