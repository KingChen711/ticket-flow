namespace TicketFlow.Ordering.Domain.Orders;

/// <summary>
/// Order-stream milestone appended when payment succeeds (saga: AwaitingPayment ->
/// AwaitingTickets). Named "OrderPaymentAuthorized", not "PaymentProcessed" — the payment
/// service's integration event is already named
/// TicketFlow.Contracts.Messaging.Payment.PaymentProcessed; reusing that name here for a
/// different type would be the exact naming hazard OrderSeatsConfirmed's doc comment describes.
/// </summary>
public sealed record OrderPaymentAuthorized(Guid OrderId, Guid PaymentId, DateTimeOffset AuthorizedAt);
