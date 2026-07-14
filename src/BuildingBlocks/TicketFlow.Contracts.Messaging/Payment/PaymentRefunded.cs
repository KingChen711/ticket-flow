namespace TicketFlow.Contracts.Messaging.Payment;

/// <summary>
/// Event published by the payment consumer after a successful refund.
/// Sent in response to <see cref="RefundPayment"/> (compensation).
/// The PurchaseSaga transitions <c>Compensating_Refund → Compensating_Seats</c> and sends
/// <c>ReleaseSeats</c>.
/// </summary>
public sealed record PaymentRefunded(
    Guid OrderId,
    Guid PaymentId,
    decimal Amount,
    DateTimeOffset RefundedAt);
