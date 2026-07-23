using MassTransit;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Payment.Application.Payments;

namespace TicketFlow.Payment.Api.Consumers;

public sealed class CancelPaymentForEventConsumer(PaymentProcessingService payments)
    : IConsumer<CancelPaymentForEvent>
{
    public async Task Consume(ConsumeContext<CancelPaymentForEvent> context)
    {
        var message = context.Message;
        var stop = await payments.StopForEventAsync(
            new StopPaymentForEventRequest(
                message.OrderId,
                message.WorkflowId,
                message.Amount,
                message.Currency,
                message.OperationKey),
            context.CancellationToken);

        if (stop.Decision == StopPaymentDecision.Unknown)
            throw new PaymentOutcomeUnknownException(message.OrderId, stop.Reason);

        var refunded = false;
        if (stop.Decision == StopPaymentDecision.RefundRequired)
        {
            var refund = await payments.RefundAsync(
                new RefundPaymentRequest(
                    message.OrderId,
                    stop.PaymentId!.Value,
                    message.Amount,
                    message.Currency,
                    $"{message.OperationKey}:refund"),
                context.CancellationToken);
            if (refund.Decision == PaymentDecision.Unknown)
                throw new PaymentOutcomeUnknownException(message.OrderId, refund.FailureReason);
            if (refund.Decision != PaymentDecision.Authorized)
                throw new InvalidOperationException(
                    $"Provider did not confirm the refund for cancelled order '{message.OrderId}': {refund.FailureReason}");
            refunded = true;
        }

        await context.Publish(new PaymentStoppedForEvent(
            message.OrderId,
            message.WorkflowId,
            stop.PaymentId,
            refunded,
            DateTimeOffset.UtcNow,
            message.OperationKey));
        await payments.CompleteEventCancellationAsync(
            message.OrderId,
            message.WorkflowId,
            context.CancellationToken);
    }
}
