using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>
/// Final compensation milestone (Commit 9), shared by all 3 failure branches the saga can end
/// on (SeatsConfirmationFailed, PaymentFailed -> released, TicketingFailed -> refunded ->
/// released) — each publishes the same OrderCancelledIntegrationEvent once compensation is done,
/// so there's exactly one place that appends the (Phase 4) OrderCancelled domain event, bypassing
/// Order.Cancel()'s guard the same way OrderCompletedConsumer bypasses Confirm()'s.
/// </summary>
public sealed class OrderCancelledConsumer(IOrderStore store, TimeProvider timeProvider, ILogger<OrderCancelledConsumer> logger)
    : IConsumer<OrderCancelledIntegrationEvent>
{
    public async Task Consume(ConsumeContext<OrderCancelledIntegrationEvent> context)
    {
        var order = await store.LoadAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("OrderCancelledIntegrationEvent received for unknown OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        store.Append(context.Message.OrderId, new OrderCancelled(context.Message.OrderId, context.Message.Reason, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
