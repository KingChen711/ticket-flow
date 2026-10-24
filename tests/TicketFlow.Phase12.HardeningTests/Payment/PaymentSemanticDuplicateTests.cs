using System.Collections.Concurrent;
using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Application.Payments;
using TicketFlow.Payment.Domain.Payments;
using TicketFlow.Phase12.HardeningTests.Support;

namespace TicketFlow.Phase12.HardeningTests.Payment;

/// <summary>
/// Money invariants for Commit 6. Every test here answers one question: after this sequence, how
/// many times did the provider actually move money?
/// </summary>
public sealed class PaymentSemanticDuplicateTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Distinct_messages_for_one_OrderId_must_execute_exactly_one_provider_charge()
    {
        var orderId = Guid.NewGuid();
        var repository = new InMemoryPaymentAttemptRepository();
        var sideEffect = new ControlledSideEffect<GatewayChargeResult>(
            () => GatewayChargeResult.Authorized($"gw-{Guid.NewGuid():N}"));
        var gateway = new ControlledPaymentGatewayFake(sideEffect);
        var request = new ProcessPaymentRequest(orderId, 500_000m, "VND", "CreditCard", $"charge:{orderId:N}");

        // Two independent consumers, two distinct envelopes, one order.
        //
        // A rendezvous barrier inside the provider call was tried first and deadlocked — which is
        // itself the result under test: the per-order claim serializes the two callers so they
        // CANNOT both be inside the provider at once. The assertion is therefore on the provider's
        // call count, not on observing concurrency at the barrier.
        var first = NewService(repository, gateway);
        var second = NewService(repository, gateway);
        var results = await Task.WhenAll(
            first.ProcessAsync(request),
            second.ProcessAsync(request));

        Assert.All(results, result => Assert.Equal(PaymentDecision.Authorized, result.Decision));
        Assert.Equal(1, sideEffect.SuccessfulSideEffects);
        Assert.Equal(1, gateway.LedgerEntries);
        Assert.Single(repository.Attempts);
        // Both callers must report the same payment identity, not two competing ones.
        Assert.Equal(results[0].PaymentId, results[1].PaymentId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Hundred_concurrent_duplicates_for_one_order_must_charge_once()
    {
        var orderId = Guid.NewGuid();
        var repository = new InMemoryPaymentAttemptRepository();
        var sideEffect = new ControlledSideEffect<GatewayChargeResult>(
            () => GatewayChargeResult.Authorized($"gw-{Guid.NewGuid():N}"));
        var gateway = new ControlledPaymentGatewayFake(sideEffect);
        var request = new ProcessPaymentRequest(orderId, 250_000m, "VND", "CreditCard", $"charge:{orderId:N}");

        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => NewService(repository, gateway).ProcessAsync(request)));

        Assert.Equal(1, sideEffect.SuccessfulSideEffects);
        Assert.Single(repository.Attempts);
        Assert.All(results, result => Assert.Equal(PaymentDecision.Authorized, result.Decision));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_lost_response_must_stay_Unknown_and_must_never_be_reported_as_declined()
    {
        var orderId = Guid.NewGuid();
        var repository = new InMemoryPaymentAttemptRepository();
        var sideEffect = new ControlledSideEffect<GatewayChargeResult>(
            () => GatewayChargeResult.Authorized("unreachable"),
            ControlledSideEffectOutcome.SideEffectSucceededButResponseLost);
        var gateway = new ControlledPaymentGatewayFake(sideEffect);
        var request = new ProcessPaymentRequest(orderId, 500_000m, "VND", "CreditCard", $"charge:{orderId:N}");

        var decision = await NewService(repository, gateway).ProcessAsync(request);

        // The provider took the money; we simply never heard back. Calling that "declined" would
        // release the seats and cancel a paid order.
        Assert.Equal(PaymentDecision.Unknown, decision.Decision);
        Assert.Equal(1, sideEffect.SuccessfulSideEffects);
        Assert.Equal(PaymentAttemptState.Unknown, Assert.Single(repository.Attempts).State);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Reconciling_an_Unknown_outcome_must_resolve_by_lookup_without_charging_again()
    {
        var orderId = Guid.NewGuid();
        var repository = new InMemoryPaymentAttemptRepository();
        var sideEffect = new ControlledSideEffect<GatewayChargeResult>(
            () => GatewayChargeResult.Authorized("unreachable"),
            ControlledSideEffectOutcome.SideEffectSucceededButResponseLost);
        var gateway = new ControlledPaymentGatewayFake(sideEffect);
        var request = new ProcessPaymentRequest(orderId, 500_000m, "VND", "CreditCard", $"charge:{orderId:N}");
        var unknown = await NewService(repository, gateway).ProcessAsync(request);
        Assert.Equal(PaymentDecision.Unknown, unknown.Decision);

        var reconciled = await NewService(repository, gateway).ProcessAsync(request);

        Assert.Equal(PaymentDecision.Authorized, reconciled.Decision);
        // The truth came from the provider's ledger, not from a second charge.
        Assert.Equal(1, sideEffect.SuccessfulSideEffects);
        Assert.Equal(PaymentAttemptState.Authorized, Assert.Single(repository.Attempts).State);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task An_Unknown_outcome_the_provider_never_recorded_must_resolve_to_declined()
    {
        var orderId = Guid.NewGuid();
        var repository = new InMemoryPaymentAttemptRepository();
        var attempt = PaymentAttempt.Claim(orderId, $"charge:{orderId:N}", 400_000m, "VND", DateTimeOffset.UtcNow);
        attempt.MarkUnknown("connection reset", DateTimeOffset.UtcNow);
        repository.Seed(attempt);
        var sideEffect = new ControlledSideEffect<GatewayChargeResult>(
            () => GatewayChargeResult.Authorized("must-not-be-called"));
        var gateway = new ControlledPaymentGatewayFake(sideEffect);

        var decision = await NewService(repository, gateway).ProcessAsync(
            new ProcessPaymentRequest(orderId, 400_000m, "VND", "CreditCard", attempt.OperationKey));

        // No ledger entry for this key means the charge never landed — now it is safe to decline
        // and let compensation run.
        Assert.Equal(PaymentDecision.Declined, decision.Decision);
        Assert.Equal(0, sideEffect.Attempts);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Duplicate_refunds_must_call_the_provider_exactly_once()
    {
        var orderId = Guid.NewGuid();
        var repository = new InMemoryPaymentAttemptRepository();
        var chargeSideEffect = new ControlledSideEffect<GatewayChargeResult>(
            () => GatewayChargeResult.Authorized($"gw-{Guid.NewGuid():N}"));
        var gateway = new ControlledPaymentGatewayFake(chargeSideEffect);
        var charge = await NewService(repository, gateway).ProcessAsync(
            new ProcessPaymentRequest(orderId, 500_000m, "VND", "CreditCard", $"charge:{orderId:N}"));
        Assert.Equal(PaymentDecision.Authorized, charge.Decision);

        var refundRequest = new RefundPaymentRequest(
            orderId, charge.PaymentId, 500_000m, "VND", $"refund:{orderId:N}");
        var first = await NewService(repository, gateway).RefundAsync(refundRequest);
        var replay = await NewService(repository, gateway).RefundAsync(refundRequest);

        Assert.Equal(PaymentDecision.Authorized, first.Decision);
        Assert.Equal(PaymentDecision.Authorized, replay.Decision);
        Assert.Equal(first.RefundId, replay.RefundId);
        Assert.Equal(first.GatewayReference, replay.GatewayReference);
        // One charge + one refund, never two of either.
        Assert.Equal(2, chargeSideEffect.SuccessfulSideEffects);
        Assert.Single(repository.Refunds);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Refunding_an_order_that_was_never_charged_must_not_call_the_provider()
    {
        var orderId = Guid.NewGuid();
        var repository = new InMemoryPaymentAttemptRepository();
        var sideEffect = new ControlledSideEffect<GatewayChargeResult>(
            () => GatewayChargeResult.Authorized("must-not-be-called"));
        var gateway = new ControlledPaymentGatewayFake(sideEffect);

        var decision = await NewService(repository, gateway).RefundAsync(
            new RefundPaymentRequest(orderId, Guid.NewGuid(), 500_000m, "VND", $"refund:{orderId:N}"));

        Assert.Equal(PaymentDecision.Declined, decision.Decision);
        Assert.Equal(0, sideEffect.Attempts);
        Assert.Empty(repository.Refunds);
    }

    private static PaymentProcessingService NewService(
        InMemoryPaymentAttemptRepository repository,
        IPaymentGatewayClient gateway) =>
        new(repository, gateway, repository.UnitOfWork, TimeProvider.System);
}

/// <summary>
/// Models the two properties that make the production claim work: a unique OrderId constraint and
/// a lock that serializes callers for one order. Commits are explicit so a test can observe what
/// was durable at the moment the provider was called.
/// </summary>
internal sealed class InMemoryPaymentAttemptRepository : IPaymentAttemptRepository
{
    private readonly ConcurrentDictionary<Guid, PaymentAttempt> _attempts = [];
    private readonly ConcurrentDictionary<Guid, PaymentRefund> _refunds = [];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _orderLocks = [];
    private readonly AsyncLocal<PaymentAttempt?> _pendingAttempt = new();
    private readonly AsyncLocal<PaymentRefund?> _pendingRefund = new();

    public IReadOnlyCollection<PaymentAttempt> Attempts => _attempts.Values.ToArray();
    public IReadOnlyCollection<PaymentRefund> Refunds => _refunds.Values.ToArray();
    public IUnitOfWork UnitOfWork => new InMemoryPaymentUnitOfWork(this);

    public void Seed(PaymentAttempt attempt) => _attempts[attempt.OrderId] = attempt;

    public async Task<IAsyncDisposable> AcquireOrderLockAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var gate = _orderLocks.GetOrAdd(orderId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Release(gate);
    }

    public Task<PaymentAttempt?> GetAttemptByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_attempts.GetValueOrDefault(orderId));

    public Task<PaymentAttempt?> GetAttemptByIdAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_attempts.Values.SingleOrDefault(attempt => attempt.Id == attemptId));

    public Task<IReadOnlyList<PaymentAttempt>> GetUnresolvedAttemptsAsync(
        int maxCount,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PaymentAttempt>>(
            _attempts.Values.Where(attempt => !attempt.IsTerminal).Take(maxCount).ToList());

    public void AddAttempt(PaymentAttempt attempt) => _pendingAttempt.Value = attempt;

    public Task<PaymentRefund?> GetRefundByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_refunds.GetValueOrDefault(orderId));

    public Task<IReadOnlyList<PaymentRefund>> GetUnresolvedRefundsAsync(
        int maxCount,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PaymentRefund>>(
            _refunds.Values.Where(refund => !refund.IsTerminal).Take(maxCount).ToList());

    public void AddRefund(PaymentRefund refund) => _pendingRefund.Value = refund;

    public bool IsUniqueViolation(Exception exception) => exception is DuplicateOrderClaimException;

    internal void Commit()
    {
        if (_pendingAttempt.Value is { } attempt)
        {
            _pendingAttempt.Value = null;
            if (!_attempts.TryAdd(attempt.OrderId, attempt))
                throw new DuplicateOrderClaimException(attempt.OrderId);
        }

        if (_pendingRefund.Value is { } refund)
        {
            _pendingRefund.Value = null;
            if (!_refunds.TryAdd(refund.OrderId, refund))
                throw new DuplicateOrderClaimException(refund.OrderId);
        }
    }

    private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class DuplicateOrderClaimException(Guid orderId)
    : Exception($"Order '{orderId}' already has a claim.");

internal sealed class InMemoryPaymentUnitOfWork(InMemoryPaymentAttemptRepository repository) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        repository.Commit();
        return Task.CompletedTask;
    }
}
