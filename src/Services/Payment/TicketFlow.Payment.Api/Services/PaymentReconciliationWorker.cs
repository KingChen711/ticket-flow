using MassTransit;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Application.Payments;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Api.Services;

public sealed class PaymentReconciliationOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// After this many failed lookups an ambiguous operation stops being retried silently and is
    /// surfaced for a human. Money that may or may not have moved is exactly the case where
    /// "keep retrying forever" is indistinguishable from "nobody noticed".
    /// </summary>
    public int ManualReviewAfterAttempts { get; set; } = 10;
}

/// <summary>
/// Resolves ambiguous charges and refunds by asking the provider what actually happened, then
/// publishes the outcome the consumer deliberately withheld. Without this worker an Unknown
/// attempt would sit forever and its saga with it — the consumer's refusal to guess is only safe
/// because something else is responsible for finding out.
/// </summary>
public sealed class PaymentReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    PaymentReconciliationOptions options,
    TimeProvider timeProvider,
    ILogger<PaymentReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Reconciliation must never take the service down; the next pass retries.
                logger.LogError(exception, "[payment] reconciliation pass failed.");
            }

            try
            {
                await Task.Delay(options.Interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPaymentAttemptRepository>();
        var payments = scope.ServiceProvider.GetRequiredService<PaymentProcessingService>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        foreach (var attempt in await repository.GetUnresolvedAttemptsAsync(options.BatchSize, cancellationToken))
        {
            if (ShouldEscalate(attempt.ReconcileAttempts, "charge", attempt.OrderId))
                continue;

            var decision = await payments.ProcessAsync(
                new ProcessPaymentRequest(
                    attempt.OrderId,
                    attempt.Amount,
                    attempt.Currency,
                    "CreditCard",
                    attempt.OperationKey),
                cancellationToken);

            switch (decision.Decision)
            {
                case PaymentDecision.Authorized:
                    PaymentOperationMetrics.RecordReconcile("charge", "authorized");
                    await publisher.Publish(
                        new PaymentProcessed(
                            decision.OrderId,
                            decision.PaymentId,
                            decision.Amount,
                            decision.DecidedAt,
                            decision.Currency,
                            decision.GatewayReference,
                            attempt.OperationKey),
                        cancellationToken);
                    break;
                case PaymentDecision.Declined:
                    PaymentOperationMetrics.RecordReconcile("charge", "declined");
                    await publisher.Publish(
                        new PaymentFailed(
                            decision.OrderId,
                            decision.FailureReason ?? "The provider has no record of this charge.",
                            attempt.OperationKey),
                        cancellationToken);
                    break;
                default:
                    PaymentOperationMetrics.RecordReconcile("charge", "still_unknown");
                    break;
            }
        }

        foreach (var refund in await repository.GetUnresolvedRefundsAsync(options.BatchSize, cancellationToken))
        {
            if (ShouldEscalate(refund.ReconcileAttempts, "refund", refund.OrderId))
                continue;

            var decision = await payments.RefundAsync(
                new RefundPaymentRequest(
                    refund.OrderId,
                    refund.PaymentId,
                    refund.Amount,
                    refund.Currency,
                    refund.OperationKey),
                cancellationToken);

            if (decision.Decision == PaymentDecision.Unknown)
            {
                PaymentOperationMetrics.RecordReconcile("refund", "still_unknown");
                continue;
            }

            PaymentOperationMetrics.RecordReconcile(
                "refund",
                decision.Decision == PaymentDecision.Authorized ? "refunded" : "declined");
            await publisher.Publish(
                new PaymentRefunded(
                    decision.OrderId,
                    decision.PaymentId,
                    decision.Amount,
                    decision.DecidedAt,
                    decision.Currency,
                    decision.GatewayReference,
                    refund.OperationKey),
                cancellationToken);
        }
    }

    private bool ShouldEscalate(int reconcileAttempts, string kind, Guid orderId)
    {
        if (reconcileAttempts < options.ManualReviewAfterAttempts)
            return false;

        PaymentOperationMetrics.RecordManualReview(kind);
        logger.LogError(
            "[payment] {Kind} for order {OrderId} is still ambiguous after {Attempts} reconciliation attempts and " +
            "needs manual review; automatic retries stopped.",
            kind,
            orderId,
            reconcileAttempts);
        return true;
    }
}
