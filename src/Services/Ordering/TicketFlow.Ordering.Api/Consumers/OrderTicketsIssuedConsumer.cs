using MassTransit;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>See OrderSeatsConfirmedConsumer's doc comment — same shape (incl. the Load-first guard), next milestone.</summary>
public sealed class OrderTicketsIssuedConsumer(IOrderStore store, TimeProvider timeProvider, ILogger<OrderTicketsIssuedConsumer> logger)
    : IConsumer<TicketsIssued>
{
    public async Task Consume(ConsumeContext<TicketsIssued> context)
    {
        var order = await store.LoadAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("TicketsIssued received for unknown OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        store.Append(
            context.Message.OrderId,
            new OrderTicketsIssued(context.Message.OrderId, context.Message.TicketPdfUrls, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
