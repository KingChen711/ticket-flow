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
    public DateTimeOffset? CancellationRequestedAt { get; private set; }
    public Guid? CancellationWorkflowId { get; private set; }
    public string? CancellationOperationKey { get; private set; }
    public DateTimeOffset? CancellationCompletedAt { get; private set; }

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

    public static PaymentAttempt CancelBeforeCharge(
        Guid orderId,
        string operationKey,
        decimal amount,
        string currency,
        Guid workflowId,
        string cancellationOperationKey,
        DateTimeOffset now)
    {
        var attempt = Claim(orderId, operationKey, amount, currency, now);
        attempt.RequestCancellation(workflowId, cancellationOperationKey, now);
        attempt.Decline("Payment stopped because the event was cancelled.", now);
        return attempt;
    }

    public void RequestCancellation(Guid workflowId, string operationKey, DateTimeOffset now)
    {
        if (workflowId == Guid.Empty)
            throw new ArgumentException("Cancellation workflow id is required.", nameof(workflowId));
        if (CancellationWorkflowId is { } existingWorkflow && existingWorkflow != workflowId)
            throw new InvalidOperationException(
                $"Payment attempt '{Id}' is already attached to cancellation workflow '{existingWorkflow}'.");
        if (CancellationOperationKey is { } existingKey &&
            !string.Equals(existingKey, operationKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Payment attempt '{Id}' is already attached to another cancellation operation.");

        CancellationWorkflowId ??= workflowId;
        CancellationOperationKey ??= Guard.NotNullOrEmpty(operationKey, nameof(operationKey)).Trim();
        CancellationRequestedAt ??= now;
    }

    public void CompleteCancellation(Guid workflowId, DateTimeOffset now)
    {
        if (CancellationWorkflowId != workflowId)
            throw new InvalidOperationException(
                $"Payment attempt '{Id}' is not attached to cancellation workflow '{workflowId}'.");
        CancellationCompletedAt ??= now;
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
