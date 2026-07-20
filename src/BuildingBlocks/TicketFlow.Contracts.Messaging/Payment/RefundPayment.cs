namespace TicketFlow.Contracts.Messaging.Payment;

/// <summary>
/// Compensation command sent by the PurchaseSaga when ticketing fails after payment was already
/// charged. The payment service consumer reverses the charge idempotently (dedup by
/// <see cref="OrderId"/>) and publishes <see cref="PaymentRefunded"/>.
/// </summary>
public sealed record RefundPayment(
    Guid OrderId,
    Guid PaymentId,
    decimal Amount,
    string Currency = "VND",
    string? OperationKey = null);
