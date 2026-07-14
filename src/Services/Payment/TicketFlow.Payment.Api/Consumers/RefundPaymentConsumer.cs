using MassTransit;
using TicketFlow.Contracts.Messaging.Payment;

namespace TicketFlow.Payment.Api.Consumers;

/// <summary>Stub for Commit 2 — see ProcessPaymentConsumer's doc comment.</summary>
public sealed class RefundPaymentConsumer(ILogger<RefundPaymentConsumer> logger) : IConsumer<RefundPayment>
{
    public Task Consume(ConsumeContext<RefundPayment> context)
    {
        var message = context.Message;
        logger.LogInformation(
            "[payment] received RefundPayment for order {OrderId}, payment {PaymentId}",
            message.OrderId,
            message.PaymentId);

        return Task.CompletedTask;
    }
}
