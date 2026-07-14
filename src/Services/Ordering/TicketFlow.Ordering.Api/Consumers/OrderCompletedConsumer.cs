using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>
/// Final milestone. Reuses the existing <see cref="OrderConfirmed"/> domain event (Phase 4)
/// rather than introducing a new "OrderCompleted" domain event — that name is already taken by
/// the integration event this consumer handles (TicketFlow.Contracts.Messaging.Ordering.
/// OrderCompleted), and "an order the saga drove all the way through is Confirmed" is exactly
/// OrderConfirmed's existing meaning. This bypasses Order.Confirm()'s guard on purpose — see that
/// method's doc comment: the guard is for the admin-override manual path, not this saga-trusted
/// one, and by the time this runs Status is TicketsIssued, not Pending. Load-first guard: see
/// OrderSeatsConfirmedConsumer's doc comment.
/// </summary>
public sealed class OrderCompletedConsumer(IOrderStore store, TimeProvider timeProvider, ILogger<OrderCompletedConsumer> logger)
    : IConsumer<OrderCompleted>
{
    public async Task Consume(ConsumeContext<OrderCompleted> context)
    {
        var order = await store.LoadAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("OrderCompleted received for unknown OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        store.Append(context.Message.OrderId, new OrderConfirmed(context.Message.OrderId, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
