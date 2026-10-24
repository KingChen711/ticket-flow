using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Application.Payments;

public sealed record ProcessPaymentRequest(
    Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod,
    string? OperationKey = null);

public sealed record RefundPaymentRequest(
    Guid OrderId,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string? OperationKey = null);

public sealed record StopPaymentForEventRequest(
    Guid OrderId,
    Guid WorkflowId,
    decimal Amount,
    string Currency,
    string OperationKey);

public enum StopPaymentDecision
{
    Stopped,
    RefundRequired,
    Unknown,
}

public sealed record StopPaymentForEventResult(
    StopPaymentDecision Decision,
    Guid? PaymentId,
    string? Reason);

public enum PaymentDecision
{
    Authorized,
    Declined,

    /// <summary>Outcome ambiguous. The caller must publish nothing and must not compensate.</summary>
    Unknown,
}

public sealed record PaymentDecisionResult(
    PaymentDecision Decision,
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string? GatewayReference,
    string? FailureReason,
    DateTimeOffset DecidedAt);

public sealed record RefundDecisionResult(
    PaymentDecision Decision,
    Guid RefundId,
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string? GatewayReference,
    string? FailureReason,
    DateTimeOffset DecidedAt);

/// <summary>
/// Durable charge/refund state machine (Phase 12 Commit 6). Replaces the
/// read-database-then-call-provider shape in the consumers, which had two defects that only
/// showed up under load or failure: two messages for one order could both pass the existence
/// check and charge twice, and every transport-level failure was recorded as a decline.
///
/// The order of operations here is the whole point:
/// claim (insert + commit) → call provider with that claim's key → record the real outcome.
/// Anything that interrupts the middle step leaves a Pending/Unknown row that reconciliation can
/// resolve by asking the provider, instead of leaving no trace at all.
/// </summary>
public sealed class PaymentProcessingService(
    IPaymentAttemptRepository attempts,
    IPaymentGatewayClient gateway,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<PaymentDecisionResult> ProcessAsync(
        ProcessPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        await using var orderLock = await attempts.AcquireOrderLockAsync(request.OrderId, cancellationToken);

        var attempt = await attempts.GetAttemptByOrderAsync(request.OrderId, cancellationToken);
        if (attempt is null)
        {
            attempt = PaymentAttempt.Claim(
                request.OrderId,
                OperationKeyFor(request),
                request.Amount,
                request.Currency,
                timeProvider.GetUtcNow());
            attempts.AddAttempt(attempt);

            try
            {
                // Committed BEFORE the provider call. If this throws on the unique index, another
                // consumer already owns this order's charge and we must never call the provider.
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (attempts.IsUniqueViolation(exception))
            {
                PaymentOperationMetrics.RecordCharge("claim_lost");
                var winner = await attempts.GetAttemptByOrderAsync(request.OrderId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"A concurrent charge claim for order '{request.OrderId}' was reported but cannot be read back.",
                        exception);
                return await ResolveAsync(winner, cancellationToken);
            }
        }
        else if (attempt.IsTerminal)
        {
            PaymentOperationMetrics.RecordCharge("replayed");
            return Map(attempt);
        }

        return await ResolveAsync(attempt, cancellationToken);
    }

    /// <summary>
    /// Serializes against charge processing, persists a no-charge tombstone when cancellation
    /// wins first, and resolves an uncertain in-flight charge before deciding whether a refund
    /// is required.
    /// </summary>
    public async Task<StopPaymentForEventResult> StopForEventAsync(
        StopPaymentForEventRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.OrderId == Guid.Empty || request.WorkflowId == Guid.Empty)
            throw new ArgumentException("Order id and workflow id are required.", nameof(request));
        if (request.Amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Payment amount must be positive.");

        await using var orderLock = await attempts.AcquireOrderLockAsync(request.OrderId, cancellationToken);
        var attempt = await attempts.GetAttemptByOrderAsync(request.OrderId, cancellationToken);
        if (attempt is null)
        {
            attempt = PaymentAttempt.CancelBeforeCharge(
                request.OrderId,
                $"{request.OperationKey}:tombstone",
                request.Amount,
                request.Currency,
                request.WorkflowId,
                request.OperationKey,
                timeProvider.GetUtcNow());
            attempts.AddAttempt(attempt);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new StopPaymentForEventResult(StopPaymentDecision.Stopped, attempt.Id, null);
        }

        attempt.RequestCancellation(request.WorkflowId, request.OperationKey, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (!attempt.IsTerminal)
        {
            // ResolveAsync switches to provider lookup when cancellation was requested. It will
            // never originate a new charge from this point.
            var resolved = await ResolveAsync(attempt, cancellationToken);
            if (resolved.Decision == PaymentDecision.Unknown)
                return new StopPaymentForEventResult(StopPaymentDecision.Unknown, attempt.Id, resolved.FailureReason);
        }

        return attempt.State == PaymentAttemptState.Authorized
            ? new StopPaymentForEventResult(StopPaymentDecision.RefundRequired, attempt.Id, null)
            : new StopPaymentForEventResult(StopPaymentDecision.Stopped, attempt.Id, attempt.FailureReason);
    }

    /// <summary>
    /// Called only after PaymentStoppedForEvent has been placed in the EF bus outbox. The marker
    /// and acknowledgement therefore commit together; reconciliation keeps selecting the attempt
    /// until that durable hand-off succeeds.
    /// </summary>
    public async Task CompleteEventCancellationAsync(
        Guid orderId,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await attempts.AcquireOrderLockAsync(orderId, cancellationToken);
        var attempt = await attempts.GetAttemptByOrderAsync(orderId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment attempt for order '{orderId}' was not found.");
        attempt.CompleteCancellation(workflowId, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Drives a claimed-but-undecided attempt to a decision, charging only if the
    /// provider has no record of this key yet.</summary>
    private async Task<PaymentDecisionResult> ResolveAsync(
        PaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (attempt.IsTerminal)
        {
            PaymentOperationMetrics.RecordCharge("replayed");
            return Map(attempt);
        }

        GatewayChargeResult result;
        if (attempt.State == PaymentAttemptState.Unknown || attempt.CancellationRequestedAt is not null)
        {
            // Never re-charge an ambiguous attempt. Ask first; a null answer proves nothing landed.
            attempt.RecordReconcileAttempt(timeProvider.GetUtcNow());
            var lookup = await SafeLookupChargeAsync(attempt.OperationKey, cancellationToken);
            result = lookup ?? GatewayChargeResult.Declined(
                attempt.CancellationRequestedAt is null
                    ? "The provider has no record of this charge."
                    : "Payment stopped because the event was cancelled before the provider charged it.");
        }
        else
        {
            result = await SafeChargeAsync(attempt, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        switch (result.Outcome)
        {
            case GatewayChargeOutcome.Authorized:
                attempt.Authorize(result.GatewayReference!, now);
                PaymentOperationMetrics.RecordCharge("authorized");
                break;
            case GatewayChargeOutcome.Declined:
                attempt.Decline(result.FailureReason ?? "The provider declined the charge.", now);
                PaymentOperationMetrics.RecordCharge("declined");
                break;
            default:
                attempt.MarkUnknown(result.FailureReason ?? "The provider outcome is unknown.", now);
                PaymentOperationMetrics.RecordCharge("unknown");
                break;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Map(attempt);
    }

    public async Task<RefundDecisionResult> RefundAsync(
        RefundPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.OrderId == Guid.Empty)
            throw new ArgumentException("Order id is required.", nameof(request));

        await using var orderLock = await attempts.AcquireOrderLockAsync(request.OrderId, cancellationToken);

        // Resolved by OrderId, not by the PaymentId in the message: the id minted for a Phase-5
        // PaymentTransaction and the id of a Phase-12 PaymentAttempt are different values for the
        // same business fact, and the order is the stable identity across both.
        var attempt = await attempts.GetAttemptByOrderAsync(request.OrderId, cancellationToken);
        if (attempt is null || attempt.State != PaymentAttemptState.Authorized)
        {
            PaymentOperationMetrics.RecordRefund("nothing_to_refund");
            return new RefundDecisionResult(
                PaymentDecision.Declined,
                Guid.Empty,
                request.PaymentId,
                request.OrderId,
                request.Amount,
                request.Currency,
                null,
                attempt is null
                    ? "No authorized charge exists for this order."
                    : $"The charge for this order is {attempt.State}, so there is nothing to refund.",
                timeProvider.GetUtcNow());
        }

        var refund = await attempts.GetRefundByOrderAsync(request.OrderId, cancellationToken);
        if (refund is null)
        {
            refund = PaymentRefund.Claim(
                attempt.Id,
                request.OrderId,
                RefundOperationKeyFor(request),
                request.Amount,
                request.Currency,
                timeProvider.GetUtcNow());
            attempts.AddRefund(refund);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (attempts.IsUniqueViolation(exception))
            {
                PaymentOperationMetrics.RecordRefund("claim_lost");
                refund = await attempts.GetRefundByOrderAsync(request.OrderId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"A concurrent refund claim for order '{request.OrderId}' was reported but cannot be read back.",
                        exception);
            }
        }

        return await ResolveRefundAsync(refund, attempt, cancellationToken);
    }

    private async Task<RefundDecisionResult> ResolveRefundAsync(
        PaymentRefund refund,
        PaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (refund.IsTerminal)
        {
            PaymentOperationMetrics.RecordRefund("replayed");
            return Map(refund);
        }

        GatewayChargeResult result;
        if (refund.State == PaymentRefundState.Unknown)
        {
            refund.RecordReconcileAttempt(timeProvider.GetUtcNow());
            var lookup = await SafeLookupRefundAsync(refund.OperationKey, cancellationToken);
            result = lookup ?? GatewayChargeResult.Declined("The provider has no record of this refund.");
        }
        else
        {
            result = await SafeRefundAsync(refund, attempt, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        switch (result.Outcome)
        {
            case GatewayChargeOutcome.Authorized:
                refund.Complete(result.GatewayReference!, now);
                PaymentOperationMetrics.RecordRefund("refunded");
                break;
            case GatewayChargeOutcome.Declined:
                refund.Decline(result.FailureReason ?? "The provider declined the refund.", now);
                PaymentOperationMetrics.RecordRefund("declined");
                break;
            default:
                refund.MarkUnknown(result.FailureReason ?? "The provider outcome is unknown.", now);
                PaymentOperationMetrics.RecordRefund("unknown");
                break;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Map(refund);
    }

    /// <summary>
    /// Any exception escaping the adapter is ambiguous by definition — the request may have
    /// reached the provider. It is recorded as Unknown, never as a decline.
    /// </summary>
    private async Task<GatewayChargeResult> SafeChargeAsync(
        PaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.ChargeAsync(
                new GatewayChargeRequest(
                    attempt.OrderId,
                    attempt.OperationKey,
                    attempt.Amount,
                    attempt.Currency,
                    "CreditCard"),
                cancellationToken);
        }
        catch (Exception exception)
        {
            return GatewayChargeResult.Unknown($"The charge call failed without a usable answer: {exception.Message}");
        }
    }

    private async Task<GatewayChargeResult> SafeRefundAsync(
        PaymentRefund refund,
        PaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.RefundAsync(
                new GatewayRefundRequest(
                    refund.OrderId,
                    refund.OperationKey,
                    attempt.GatewayReference!,
                    refund.Amount,
                    refund.Currency),
                cancellationToken);
        }
        catch (Exception exception)
        {
            return GatewayChargeResult.Unknown($"The refund call failed without a usable answer: {exception.Message}");
        }
    }

    private async Task<GatewayChargeResult?> SafeLookupChargeAsync(
        string operationKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.LookupChargeAsync(operationKey, cancellationToken);
        }
        catch (Exception exception)
        {
            // A failed lookup leaves the attempt Unknown for the next reconciliation pass.
            return GatewayChargeResult.Unknown($"The provider lookup failed: {exception.Message}");
        }
    }

    private async Task<GatewayChargeResult?> SafeLookupRefundAsync(
        string operationKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.LookupRefundAsync(operationKey, cancellationToken);
        }
        catch (Exception exception)
        {
            return GatewayChargeResult.Unknown($"The provider refund lookup failed: {exception.Message}");
        }
    }

    /// <summary>Stable per-order key. The saga supplies one; the fallback keeps a v1 message
    /// idempotent rather than generating a fresh key on every delivery.</summary>
    private static string OperationKeyFor(ProcessPaymentRequest request) =>
        string.IsNullOrWhiteSpace(request.OperationKey)
            ? $"charge:{request.OrderId:N}"
            : request.OperationKey.Trim();

    private static string RefundOperationKeyFor(RefundPaymentRequest request) =>
        string.IsNullOrWhiteSpace(request.OperationKey)
            ? $"refund:{request.OrderId:N}"
            : request.OperationKey.Trim();

    private static void Validate(ProcessPaymentRequest request)
    {
        if (request.OrderId == Guid.Empty)
            throw new ArgumentException("Order id is required.", nameof(request));
        if (request.Amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A charge amount must be positive.");
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
            throw new ArgumentException("Currency must be a three-letter code.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PaymentMethod))
            throw new ArgumentException("Payment method is required.", nameof(request));
    }

    private static PaymentDecisionResult Map(PaymentAttempt attempt) => new(
        attempt.State switch
        {
            PaymentAttemptState.Authorized => PaymentDecision.Authorized,
            PaymentAttemptState.Declined => PaymentDecision.Declined,
            _ => PaymentDecision.Unknown,
        },
        attempt.Id,
        attempt.OrderId,
        attempt.Amount,
        attempt.Currency,
        attempt.GatewayReference,
        attempt.FailureReason,
        attempt.CompletedAt ?? attempt.LastReconciledAt ?? attempt.CreatedAt);

    private static RefundDecisionResult Map(PaymentRefund refund) => new(
        refund.State switch
        {
            PaymentRefundState.Refunded => PaymentDecision.Authorized,
            PaymentRefundState.Declined => PaymentDecision.Declined,
            _ => PaymentDecision.Unknown,
        },
        refund.Id,
        refund.PaymentId,
        refund.OrderId,
        refund.Amount,
        refund.Currency,
        refund.GatewayReference,
        refund.FailureReason,
        refund.CompletedAt ?? refund.LastReconciledAt ?? refund.CreatedAt);
}
