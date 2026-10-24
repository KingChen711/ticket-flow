using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Payment.Application.Payments;

namespace TicketFlow.Payment.Api.Consumers;

/// <summary>
/// Compensation shell. Before Commit 6 this consumer only flipped a local status column and
/// published <c>PaymentRefunded</c> — no provider call existed anywhere, so the saga reported a
/// refunded order while the customer's money had not moved. It now drives a durable refund state
/// machine that actually calls the provider with an idempotency key.
/// </summary>
/// <remarks>
/// Same Unknown policy as <see cref="ProcessPaymentConsumer"/>: an ambiguous refund publishes
/// nothing and throws, because claiming the money went back when that is unproven is worse than
/// a stalled compensation.
/// </remarks>
public sealed class RefundPaymentConsumer(
    PaymentProcessingService payments,
    ILogger<RefundPaymentConsumer> logger) : IConsumer<RefundPayment>
{
    public async Task Consume(ConsumeContext<RefundPayment> context)
    {
        var message = context.Message;
        ContractVersionMetrics.RecordInbound(nameof(RefundPayment), message.OperationKey is null ? 0 : 1);

        var decision = await payments.RefundAsync(
            new RefundPaymentRequest(
                message.OrderId,
                message.PaymentId,
                message.Amount,
                message.Currency,
                message.OperationKey),
            context.CancellationToken);

        switch (decision.Decision)
        {
            case PaymentDecision.Authorized:
                await context.Publish(new PaymentRefunded(
                    decision.OrderId,
                    decision.PaymentId,
                    decision.Amount,
                    decision.DecidedAt,
                    decision.Currency,
                    decision.GatewayReference,
                    message.OperationKey));
                break;

            case PaymentDecision.Declined:
                // Nothing to refund (no authorized charge, or the provider refused). The saga
                // still needs to move on; the seats it wants back were never paid for.
                logger.LogWarning(
                    "[payment] refund for order {OrderId} was not performed: {Reason}",
                    decision.OrderId,
                    decision.FailureReason);
                await context.Publish(new PaymentRefunded(
                    decision.OrderId,
                    decision.PaymentId,
                    decision.Amount,
                    decision.DecidedAt,
                    decision.Currency,
                    decision.GatewayReference,
                    message.OperationKey));
                break;

            default:
                logger.LogError(
                    "[payment] refund outcome for order {OrderId} is ambiguous ({Reason}); publishing nothing until " +
                    "reconciliation proves whether the money moved.",
                    decision.OrderId,
                    decision.FailureReason);
                throw new PaymentOutcomeUnknownException(decision.OrderId, decision.FailureReason);
        }
    }
}
