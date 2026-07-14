using MassTransit;
using TicketFlow.Contracts.Messaging.Payment;

namespace TicketFlow.Payment.Api.Consumers;

/// <summary>
/// Stub for Commit 2 — proves the message reaches this service, doesn't yet call the gateway,
/// persist a PaymentTransaction, or publish PaymentProcessed/PaymentFailed. The real pipeline
/// (gateway call + persist + publish + MassTransit Inbox for idempotency) lands in Commit 7,
/// same "skeleton first, wire for real later" split used for Notification/Search in Phase 3.
/// </summary>
public sealed class ProcessPaymentConsumer(ILogger<ProcessPaymentConsumer> logger) : IConsumer<ProcessPayment>
{
    public Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var message = context.Message;
        logger.LogInformation(
            "[payment] received ProcessPayment for order {OrderId} — {Amount} via {PaymentMethod}",
            message.OrderId,
            message.Amount,
            message.PaymentMethod);

        return Task.CompletedTask;
    }
}
