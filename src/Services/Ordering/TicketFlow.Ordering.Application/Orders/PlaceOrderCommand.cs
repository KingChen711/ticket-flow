using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Orders;

public sealed record PlaceOrderCommand(
    Guid CustomerId,
    Guid CatalogEventId,
    IReadOnlyList<string> SeatIds,
    decimal TotalAmount,
    string? Currency);

public sealed class PlaceOrderCommandHandler(IOrderStore store, TimeProvider timeProvider)
    : ICommandHandler<PlaceOrderCommand, OrderResponse>
{
    public async Task<OrderResponse> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        var total = Money.Create(command.TotalAmount, command.Currency ?? "VND");
        var placed = Order.Place(command.CustomerId, command.CatalogEventId, command.SeatIds, total, timeProvider.GetUtcNow());

        // No aggregate exists yet to Load — StartStream both creates the stream and stages its
        // first event; SaveChangesAsync is what actually commits it to Postgres.
        store.StartStream(placed.OrderId, placed);
        await store.SaveChangesAsync(cancellationToken);

        return OrderResponseMapper.Map(Order.Load([placed]));
    }
}
