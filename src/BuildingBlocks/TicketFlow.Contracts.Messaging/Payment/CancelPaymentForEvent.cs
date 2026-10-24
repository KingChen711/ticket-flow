namespace TicketFlow.Contracts.Messaging.Payment;

/// <summary>
/// Durable stop/refund command used by event cancellation. Payment first records a tombstone so
/// a delayed ProcessPayment cannot charge after this command wins the per-order lock.
/// </summary>
public sealed record CancelPaymentForEvent(
    Guid OrderId,
    Guid WorkflowId,
    decimal Amount,
    string Currency,
    string OperationKey);

public sealed record PaymentStoppedForEvent(
    Guid OrderId,
    Guid WorkflowId,
    Guid? PaymentId,
    bool Refunded,
    DateTimeOffset StoppedAt,
    string OperationKey);
