using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>See OrderSeatsConfirmedConsumer's doc comment — same shape (incl. the Load-first guard), next milestone.</summary>
public sealed class OrderPaymentAuthorizedConsumer(IOrderStore store, TimeProvider timeProvider, ILogger<OrderPaymentAuthorizedConsumer> logger)
    : IConsumer<PaymentProcessed>
{
    public async Task Consume(ConsumeContext<PaymentProcessed> context)
    {
        ContractVersionMetrics.RecordInbound(nameof(PaymentProcessed), context.Message.AggregateVersion);
        var order = await store.LoadAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("PaymentProcessed received for unknown OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        store.Append(
            context.Message.OrderId,
            new OrderPaymentAuthorized(context.Message.OrderId, context.Message.PaymentId, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
