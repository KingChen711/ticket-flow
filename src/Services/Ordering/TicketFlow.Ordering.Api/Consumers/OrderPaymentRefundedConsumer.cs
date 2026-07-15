using MassTransit;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>Compensation milestone (Commit 9). Same shape as OrderSeatsConfirmedConsumer.</summary>
public sealed class OrderPaymentRefundedConsumer(IOrderStore store, TimeProvider timeProvider, ILogger<OrderPaymentRefundedConsumer> logger)
    : IConsumer<PaymentRefunded>
{
    public async Task Consume(ConsumeContext<PaymentRefunded> context)
    {
        var order = await store.LoadAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("PaymentRefunded received for unknown OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        store.Append(
            context.Message.OrderId,
            new OrderPaymentRefunded(context.Message.OrderId, context.Message.PaymentId, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
