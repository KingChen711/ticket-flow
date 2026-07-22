using TicketFlow.Domain;

namespace TicketFlow.Payment.Domain.Payments;

/// <summary>
/// The durable charge state machine (Phase 12 Commit 6), and from this commit on the authority
/// for "was this order charged". Its <see cref="Entity{T}.Id"/> is the <c>PaymentId</c> carried by
/// <c>PaymentProcessed</c>/<c>RefundPayment</c>.
///
/// The row is inserted — and committed — <em>before</em> the provider is called, so the unique
/// <c>OrderId</c> index is what actually prevents a double charge: a second concurrent consumer
/// loses the insert race and replays instead of charging. Checking the database first and then
/// calling the provider (the pre-Phase-12 shape) leaves both callers past the check before either
/// writes anything.
/// </summary>
public sealed class PaymentAttempt : Entity<Guid>
{
    public Guid OrderId { get; private set; }

    /// <summary>Stable provider idempotency key. Sent to the gateway and used for lookup.</summary>
    public string OperationKey { get; private set; } = null!;

    public PaymentAttemptState State { get; private set; }
    public string? GatewayReference { get; private set; }
    public string? FailureReason { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>How many times reconciliation has asked the provider about an Unknown outcome.
    /// Drives the manual-review deadline rather than retrying forever in silence.</summary>
    public int ReconcileAttempts { get; private set; }

    public DateTimeOffset? LastReconciledAt { get; private set; }

    public bool IsTerminal => State is PaymentAttemptState.Authorized or PaymentAttemptState.Declined;

    private PaymentAttempt() { }

    public static PaymentAttempt Claim(
        Guid orderId,
        string operationKey,
        decimal amount,
        string currency,
        DateTimeOffset now)
    {
        if (orderId == Guid.Empty)
            throw new ArgumentException("Order id is required.", nameof(orderId));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "A charge amount must be positive.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));

        return new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            OperationKey = Guard.NotNullOrEmpty(operationKey, nameof(operationKey)).Trim(),
            State = PaymentAttemptState.Pending,
            Amount = amount,
            Currency = currency.ToUpperInvariant(),
            CreatedAt = now,
        };
    }

    public void Authorize(string gatewayReference, DateTimeOffset now)
    {
        if (State == PaymentAttemptState.Authorized)
            return;

        if (State == PaymentAttemptState.Declined)
            throw new InvalidOperationException($"Attempt '{Id}' was already declined and cannot be authorized.");

        State = PaymentAttemptState.Authorized;
        GatewayReference = Guard.NotNullOrEmpty(gatewayReference, nameof(gatewayReference));
        FailureReason = null;
        CompletedAt = now;
    }

    public void Decline(string reason, DateTimeOffset now)
    {
        if (State == PaymentAttemptState.Declined)
            return;

        if (State == PaymentAttemptState.Authorized)
            throw new InvalidOperationException($"Attempt '{Id}' was already authorized and cannot be declined.");

        State = PaymentAttemptState.Declined;
        FailureReason = Guard.NotNullOrEmpty(reason, nameof(reason));
        CompletedAt = now;
    }

    /// <summary>
    /// The provider's answer never arrived. Deliberately NOT terminal and deliberately NOT a
    /// decline: no compensation may run off the back of this state.
    /// </summary>
    public void MarkUnknown(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
            return;

        State = PaymentAttemptState.Unknown;
        FailureReason = Guard.NotNullOrEmpty(reason, nameof(reason));
        LastReconciledAt = now;
    }

    public void RecordReconcileAttempt(DateTimeOffset now)
    {
        ReconcileAttempts++;
        LastReconciledAt = now;
    }
}
