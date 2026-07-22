using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Tests.Orders;

public class ConfirmOrderCommandHandlerTests
{
    private static async Task<(FakeOrderStore Store, Guid OrderId)> PlaceOrderAsync()
    {
        var (placeHandler, store, _) = PlaceOrderCommandHandlerTests.CreateHandler();
        var placed = await placeHandler.HandleAsync(
            new PlaceOrderCommand(
                Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], 100_000, "VND",
                Guid.NewGuid(), "CreditCard", "Nguyen Van A", "a@example.com"));

        return (store, placed.OrderId);
    }

    [Fact]
    public async Task HandleAsync_PendingOrder_AppendsConfirmedAndReturnsConfirmedStatus()
    {
        var (store, orderId) = await PlaceOrderAsync();
        var handler = new ConfirmOrderCommandHandler(store, TimeProvider.System);

        var response = await handler.HandleAsync(new ConfirmOrderCommand(orderId));

        Assert.Equal(nameof(OrderStatus.Confirmed), response.Status);

        // Stream accumulated both events, in order — the "tích luỹ đúng" DoD check at the
        // handler level (Commit 4/7 verify this again for real against Postgres).
        var events = store.EventsFor(orderId);
        Assert.Equal(2, events.Count);
        Assert.IsType<OrderPlaced>(events[0]);
        Assert.IsType<OrderConfirmed>(events[1]);
    }

    [Fact]
    public async Task HandleAsync_UnknownOrderId_Throws()
    {
        var handler = new ConfirmOrderCommandHandler(new FakeOrderStore(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new ConfirmOrderCommand(Guid.NewGuid())));
    }

    [Fact]
    public async Task HandleAsync_AlreadyConfirmedOrder_Throws()
    {
        var (store, orderId) = await PlaceOrderAsync();
        var handler = new ConfirmOrderCommandHandler(store, TimeProvider.System);
        await handler.HandleAsync(new ConfirmOrderCommand(orderId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new ConfirmOrderCommand(orderId)));
    }
}
