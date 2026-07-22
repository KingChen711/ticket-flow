using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Payment.Application.Payments;

namespace TicketFlow.Payment.Api.Consumers;

/// <summary>
/// Thin transport shell over <see cref="PaymentProcessingService"/>. All the idempotency and
/// outcome classification lives in the service; this consumer only translates a decision into a
/// contract event.
/// </summary>
/// <remarks>
/// An <see cref="PaymentDecision.Unknown"/> outcome publishes NOTHING and throws, so the message
/// is retried and — if it stays ambiguous — lands in the error queue while the reconciliation
/// worker keeps asking the provider. Publishing <c>PaymentFailed</c> here would be the exact bug
/// this commit exists to remove: it releases the seats and cancels an order whose money may
/// already be gone. The saga stalling in <c>AwaitingPayment</c> is the deliberate lesser evil
/// until Commit 9 adds deadlines and an explicit ManualReview state.
/// </remarks>
public sealed class ProcessPaymentConsumer(
    PaymentProcessingService payments,
    ILogger<ProcessPaymentConsumer> logger) : IConsumer<ProcessPayment>
{
    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var message = context.Message;
        ContractVersionMetrics.RecordInbound(nameof(ProcessPayment), message.PriceSnapshotVersion);

        var decision = await payments.ProcessAsync(
            new ProcessPaymentRequest(
                message.OrderId,
                message.Amount,
                message.Currency,
                message.PaymentMethod,
                message.OperationKey),
            context.CancellationToken);

        switch (decision.Decision)
        {
            case PaymentDecision.Authorized:
                await context.Publish(new PaymentProcessed(
                    decision.OrderId,
                    decision.PaymentId,
                    decision.Amount,
                    decision.DecidedAt,
                    decision.Currency,
                    decision.GatewayReference,
                    message.OperationKey));
                break;

            case PaymentDecision.Declined:
                await context.Publish(new PaymentFailed(
                    decision.OrderId,
                    decision.FailureReason ?? "The provider declined the charge.",
                    message.OperationKey));
                break;

            default:
                logger.LogError(
                    "[payment] charge outcome for order {OrderId} is ambiguous ({Reason}); publishing nothing so no " +
                    "compensation runs against a possibly-completed charge. Reconciliation will resolve it.",
                    decision.OrderId,
                    decision.FailureReason);
                throw new PaymentOutcomeUnknownException(decision.OrderId, decision.FailureReason);
        }
    }
}

public sealed class PaymentOutcomeUnknownException(Guid orderId, string? reason)
    : Exception($"The payment outcome for order '{orderId}' is unknown: {reason ?? "no provider answer"}.");
