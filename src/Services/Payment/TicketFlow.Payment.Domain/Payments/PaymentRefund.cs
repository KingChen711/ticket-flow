using TicketFlow.Domain;

namespace TicketFlow.Payment.Domain.Payments;

/// <summary>
/// Durable refund state machine (Phase 12 Commit 6). Before this commit a "refund" only flipped a
/// local status column — no provider call existed at all, so the customer's money was never
/// actually returned while the order was reported as refunded.
/// </summary>
public sealed class PaymentRefund : Entity<Guid>
{
    public Guid PaymentId { get; private set; }
    public Guid OrderId { get; private set; }
    public string OperationKey { get; private set; } = null!;
    public PaymentRefundState State { get; private set; }
    public string? GatewayReference { get; private set; }
    public string? FailureReason { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public int ReconcileAttempts { get; private set; }
    public DateTimeOffset? LastReconciledAt { get; private set; }

    public bool IsTerminal => State is PaymentRefundState.Refunded or PaymentRefundState.Declined;

    private PaymentRefund() { }

    public static PaymentRefund Claim(
        Guid paymentId,
        Guid orderId,
        string operationKey,
        decimal amount,
        string currency,
        DateTimeOffset now)
    {
        if (paymentId == Guid.Empty)
            throw new ArgumentException("Payment id is required.", nameof(paymentId));
        if (orderId == Guid.Empty)
            throw new ArgumentException("Order id is required.", nameof(orderId));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "A refund amount must be positive.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));

        return new PaymentRefund
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            OrderId = orderId,
            OperationKey = Guard.NotNullOrEmpty(operationKey, nameof(operationKey)).Trim(),
            State = PaymentRefundState.Pending,
            Amount = amount,
            Currency = currency.ToUpperInvariant(),
            CreatedAt = now,
        };
    }

    public void Complete(string gatewayReference, DateTimeOffset now)
    {
        if (State == PaymentRefundState.Refunded)
            return;

        if (State == PaymentRefundState.Declined)
            throw new InvalidOperationException($"Refund '{Id}' was already declined.");

        State = PaymentRefundState.Refunded;
        GatewayReference = Guard.NotNullOrEmpty(gatewayReference, nameof(gatewayReference));
        FailureReason = null;
        CompletedAt = now;
    }

    public void Decline(string reason, DateTimeOffset now)
    {
        if (State == PaymentRefundState.Declined)
            return;

        if (State == PaymentRefundState.Refunded)
            throw new InvalidOperationException($"Refund '{Id}' already completed.");

        State = PaymentRefundState.Declined;
        FailureReason = Guard.NotNullOrEmpty(reason, nameof(reason));
        CompletedAt = now;
    }

    public void MarkUnknown(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
            return;

        State = PaymentRefundState.Unknown;
        FailureReason = Guard.NotNullOrEmpty(reason, nameof(reason));
        LastReconciledAt = now;
    }

    public void RecordReconcileAttempt(DateTimeOffset now)
    {
        ReconcileAttempts++;
        LastReconciledAt = now;
    }
}
