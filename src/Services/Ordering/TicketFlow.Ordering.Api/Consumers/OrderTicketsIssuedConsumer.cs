using MassTransit;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>See OrderSeatsConfirmedConsumer's doc comment — same shape, next milestone.</summary>
public sealed class OrderTicketsIssuedConsumer(IOrderStore store, TimeProvider timeProvider) : IConsumer<TicketsIssued>
{
    public async Task Consume(ConsumeContext<TicketsIssued> context)
    {
        store.Append(
            context.Message.OrderId,
            new OrderTicketsIssued(context.Message.OrderId, context.Message.TicketPdfUrls, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
