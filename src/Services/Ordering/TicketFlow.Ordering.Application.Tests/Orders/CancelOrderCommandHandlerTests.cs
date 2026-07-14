using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Tests.Orders;

public class CancelOrderCommandHandlerTests
{
    private static async Task<(FakeOrderStore Store, Guid OrderId)> PlaceOrderAsync()
    {
        var store = new FakeOrderStore();
        var placeHandler = new PlaceOrderCommandHandler(store, TimeProvider.System);
        var placed = await placeHandler.HandleAsync(
            new PlaceOrderCommand(Guid.NewGuid(), Guid.NewGuid(), ["A1"], 100_000, "VND"));

        return (store, placed.OrderId);
    }

    [Fact]
    public async Task HandleAsync_PendingOrder_AppendsCancelledAndReturnsCancelledStatus()
    {
        var (store, orderId) = await PlaceOrderAsync();
        var handler = new CancelOrderCommandHandler(store, TimeProvider.System);

        var response = await handler.HandleAsync(new CancelOrderCommand(orderId, "customer changed mind"));

        Assert.Equal(nameof(OrderStatus.Cancelled), response.Status);

        var events = store.EventsFor(orderId);
        Assert.Equal(2, events.Count);
        Assert.IsType<OrderCancelled>(events[1]);
    }

    [Fact]
    public async Task HandleAsync_UnknownOrderId_Throws()
    {
        var handler = new CancelOrderCommandHandler(new FakeOrderStore(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new CancelOrderCommand(Guid.NewGuid(), "reason")));
    }

    [Fact]
    public async Task HandleAsync_AlreadyCancelledOrder_Throws()
    {
        var (store, orderId) = await PlaceOrderAsync();
        var handler = new CancelOrderCommandHandler(store, TimeProvider.System);
        await handler.HandleAsync(new CancelOrderCommand(orderId, "first cancel"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new CancelOrderCommand(orderId, "second cancel")));
    }

    [Fact]
    public async Task HandleAsync_ConfirmedOrder_Throws()
    {
        var (store, orderId) = await PlaceOrderAsync();
        var confirmHandler = new ConfirmOrderCommandHandler(store, TimeProvider.System);
        await confirmHandler.HandleAsync(new ConfirmOrderCommand(orderId));

        var cancelHandler = new CancelOrderCommandHandler(store, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cancelHandler.HandleAsync(new CancelOrderCommand(orderId, "too late")));
    }
}
