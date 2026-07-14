using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Tests.Orders;

public class PlaceOrderCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ValidCommand_StartsStreamAndReturnsPendingOrder()
    {
        var store = new FakeOrderStore();
        var handler = new PlaceOrderCommandHandler(store, TimeProvider.System);
        var customerId = Guid.NewGuid();
        var catalogEventId = Guid.NewGuid();

        var response = await handler.HandleAsync(
            new PlaceOrderCommand(customerId, catalogEventId, ["A1", "A2"], 500_000, "VND"));

        Assert.Equal(customerId, response.CustomerId);
        Assert.Equal(catalogEventId, response.CatalogEventId);
        Assert.Equal(["A1", "A2"], response.SeatIds);
        Assert.Equal(500_000, response.TotalAmount);
        Assert.Equal("VND", response.Currency);
        Assert.Equal(nameof(OrderStatus.Pending), response.Status);

        // Exactly one event staged for a brand-new stream.
        var events = store.EventsFor(response.OrderId);
        var placed = Assert.Single(events);
        Assert.IsType<OrderPlaced>(placed);
    }

    [Fact]
    public async Task HandleAsync_NoSeats_Throws()
    {
        var handler = new PlaceOrderCommandHandler(new FakeOrderStore(), TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(new PlaceOrderCommand(Guid.NewGuid(), Guid.NewGuid(), [], 100_000, "VND")));
    }

    [Fact]
    public async Task HandleAsync_NoCurrency_DefaultsToVnd()
    {
        var handler = new PlaceOrderCommandHandler(new FakeOrderStore(), TimeProvider.System);

        var response = await handler.HandleAsync(
            new PlaceOrderCommand(Guid.NewGuid(), Guid.NewGuid(), ["A1"], 100_000, Currency: null));

        Assert.Equal("VND", response.Currency);
    }
}
