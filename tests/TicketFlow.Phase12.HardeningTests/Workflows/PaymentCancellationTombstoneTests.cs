using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Application.Payments;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Phase12.HardeningTests.Workflows;

public sealed class PaymentCancellationTombstoneTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Cancellation_winning_before_delayed_process_message_prevents_charge()
    {
        var repository = new InMemoryPaymentAttempts();
        var gateway = new RecordingGateway();
        var service = CreateService(repository, gateway);
        var orderId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        var stopped = await service.StopForEventAsync(new StopPaymentForEventRequest(
            orderId, workflowId, 500_000m, "VND", $"event-cancel:{workflowId:N}:{orderId:N}:payment"));
        Assert.Equal(StopPaymentDecision.Stopped, stopped.Decision);

        var delayed = await service.ProcessAsync(new ProcessPaymentRequest(
            orderId, 500_000m, "VND", "CreditCard", $"checkout:{orderId:N}:charge"));
        Assert.Equal(PaymentDecision.Declined, delayed.Decision);
        Assert.Equal(0, gateway.ChargeCalls);

        var replay = await service.StopForEventAsync(new StopPaymentForEventRequest(
            orderId, workflowId, 500_000m, "VND", $"event-cancel:{workflowId:N}:{orderId:N}:payment"));
        Assert.Equal(StopPaymentDecision.Stopped, replay.Decision);
        Assert.Single(repository.Attempts);
        Assert.Equal(workflowId, repository.Attempts.Single().CancellationWorkflowId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Authorized_charge_is_refunded_once_under_cancellation_replay()
    {
        var repository = new InMemoryPaymentAttempts();
        var gateway = new RecordingGateway();
        var service = CreateService(repository, gateway);
        var orderId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        var charged = await service.ProcessAsync(new ProcessPaymentRequest(
            orderId, 500_000m, "VND", "CreditCard", $"checkout:{orderId:N}:charge"));
        Assert.Equal(PaymentDecision.Authorized, charged.Decision);

        for (var replayIndex = 0; replayIndex < 2; replayIndex++)
        {
            var stop = await service.StopForEventAsync(new StopPaymentForEventRequest(
                orderId, workflowId, 500_000m, "VND", $"event-cancel:{workflowId:N}:{orderId:N}:payment"));
            Assert.Equal(StopPaymentDecision.RefundRequired, stop.Decision);
            var refund = await service.RefundAsync(new RefundPaymentRequest(
                orderId,
                stop.PaymentId!.Value,
                500_000m,
                "VND",
                $"event-cancel:{workflowId:N}:{orderId:N}:payment:refund"));
            Assert.Equal(PaymentDecision.Authorized, refund.Decision);
        }

        Assert.Equal(1, gateway.ChargeCalls);
        Assert.Equal(1, gateway.RefundCalls);
        Assert.Single(repository.Refunds);

        await service.CompleteEventCancellationAsync(orderId, workflowId);
        var paymentAttempt = Assert.Single(repository.Attempts);
        Assert.Equal(workflowId, paymentAttempt.CancellationWorkflowId);
        Assert.NotNull(paymentAttempt.CancellationCompletedAt);
    }

    private static PaymentProcessingService CreateService(
        InMemoryPaymentAttempts repository,
        RecordingGateway gateway) =>
        new(repository, gateway, new NoopPaymentUnitOfWork(), TimeProvider.System);

    private sealed class InMemoryPaymentAttempts : IPaymentAttemptRepository
    {
        public List<PaymentAttempt> Attempts { get; } = [];
        public List<PaymentRefund> Refunds { get; } = [];

        public Task<IAsyncDisposable> AcquireOrderLockAsync(
            Guid orderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(NoopPaymentLock.Instance);

        public Task<PaymentAttempt?> GetAttemptByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Attempts.SingleOrDefault(attempt => attempt.OrderId == orderId));

        public Task<PaymentAttempt?> GetAttemptByIdAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Attempts.SingleOrDefault(attempt => attempt.Id == attemptId));

        public Task<IReadOnlyList<PaymentAttempt>> GetUnresolvedAttemptsAsync(
            int maxCount,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaymentAttempt>>(
                Attempts.Where(attempt => !attempt.IsTerminal).Take(maxCount).ToList());

        public void AddAttempt(PaymentAttempt attempt) => Attempts.Add(attempt);

        public Task<PaymentRefund?> GetRefundByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Refunds.SingleOrDefault(refund => refund.OrderId == orderId));

        public Task<IReadOnlyList<PaymentRefund>> GetUnresolvedRefundsAsync(
            int maxCount,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaymentRefund>>(
                Refunds.Where(refund => !refund.IsTerminal).Take(maxCount).ToList());

        public void AddRefund(PaymentRefund refund) => Refunds.Add(refund);
        public bool IsUniqueViolation(Exception exception) => false;
    }

    private sealed class RecordingGateway : IPaymentGatewayClient
    {
        private readonly Dictionary<string, GatewayChargeResult> _charges = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GatewayChargeResult> _refunds = new(StringComparer.Ordinal);
        public int ChargeCalls { get; private set; }
        public int RefundCalls { get; private set; }

        public Task<GatewayChargeResult> ChargeAsync(
            GatewayChargeRequest request,
            CancellationToken cancellationToken = default)
        {
            ChargeCalls++;
            var result = _charges.GetValueOrDefault(request.IdempotencyKey)
                ?? GatewayChargeResult.Authorized($"charge-{request.OrderId:N}");
            _charges[request.IdempotencyKey] = result;
            return Task.FromResult(result);
        }

        public Task<GatewayChargeResult?> LookupChargeAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_charges.GetValueOrDefault(idempotencyKey));

        public Task<GatewayChargeResult> RefundAsync(
            GatewayRefundRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!_refunds.TryGetValue(request.IdempotencyKey, out var result))
            {
                RefundCalls++;
                result = GatewayChargeResult.Authorized($"refund-{request.OrderId:N}");
                _refunds.Add(request.IdempotencyKey, result);
            }
            return Task.FromResult(result);
        }

        public Task<GatewayChargeResult?> LookupRefundAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_refunds.GetValueOrDefault(idempotencyKey));
    }

    private sealed class NoopPaymentUnitOfWork : IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
