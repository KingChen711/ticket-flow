using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Api.Consumers;

/// <summary>
/// Compensation step (used from Commit 9 onward once the saga has failure branches — this
/// consumer works from day one regardless, like Reservation's ReleaseSeatsConsumer). Idempotent
/// by business key (payment already Refunded → replay the result, don't call MarkRefunded again —
/// its guard only allows Succeeded → Refunded, so a second attempt must be short-circuited here,
/// not left to throw). Same MassTransit Inbox protection as ProcessPaymentConsumer for the
/// redelivered-message case, wired on this consumer's endpoint in Program.cs.
/// </summary>
public sealed class RefundPaymentConsumer(
    IPaymentRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<RefundPaymentConsumer> logger) : IConsumer<RefundPayment>
{
    public async Task Consume(ConsumeContext<RefundPayment> context)
    {
        var message = context.Message;
        ContractVersionMetrics.RecordInbound(nameof(RefundPayment), message.OperationKey is null ? 0 : 1);
        var payment = await repository.GetByIdAsync(message.PaymentId, context.CancellationToken);

        if (payment is null)
        {
            logger.LogWarning(
                "RefundPayment received for unknown PaymentId {PaymentId} (order {OrderId}) — skipping.",
                message.PaymentId,
                message.OrderId);
            return;
        }

        if (payment.Status == PaymentStatus.Refunded)
        {
            logger.LogInformation(
                "[payment] duplicate RefundPayment for order {OrderId} — already refunded, replaying result.",
                message.OrderId);
        }
        else
        {
            payment.MarkRefunded(timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(context.CancellationToken);
        }

        await context.Publish(new PaymentRefunded(payment.OrderId, payment.Id, payment.Amount.Amount, payment.RefundedAt!.Value));
    }
}
