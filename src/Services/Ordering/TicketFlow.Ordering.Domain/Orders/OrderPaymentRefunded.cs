namespace TicketFlow.Ordering.Domain.Orders;

/// <summary>
/// Order-stream compensation milestone (Commit 9) appended when payment is reversed because
/// ticketing failed after the charge already succeeded. "Order" prefix for the same reason as
/// OrderPaymentAuthorized — TicketFlow.Contracts.Messaging.Payment.PaymentRefunded already exists.
/// </summary>
public sealed record OrderPaymentRefunded(Guid OrderId, Guid PaymentId, DateTimeOffset RefundedAt);
