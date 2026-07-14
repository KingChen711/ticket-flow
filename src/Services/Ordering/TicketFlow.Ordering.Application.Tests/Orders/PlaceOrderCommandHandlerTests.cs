using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Tests.Orders;

public class PlaceOrderCommandHandlerTests
{
    private static PlaceOrderCommand ValidCommand(
        Guid? customerId = null, Guid? catalogEventId = null, IReadOnlyList<Guid>? seatIds = null,
        decimal totalAmount = 500_000, string? currency = "VND") =>
        new(
            customerId ?? Guid.NewGuid(),
            catalogEventId ?? Guid.NewGuid(),
            seatIds ?? [Guid.NewGuid(), Guid.NewGuid()],
            totalAmount,
            currency,
            Guid.NewGuid(),
            "CreditCard",
            "Nguyen Van A",
            "a@example.com");

    [Fact]
    public async Task HandleAsync_ValidCommand_StartsStreamAndReturnsPendingOrder()
    {
        var store = new FakeOrderStore();
        var publisher = new FakeIntegrationEventPublisher();
        var handler = new PlaceOrderCommandHandler(store, publisher, TimeProvider.System);
        var customerId = Guid.NewGuid();
        var catalogEventId = Guid.NewGuid();
        var seatIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        var response = await handler.HandleAsync(ValidCommand(customerId, catalogEventId, seatIds));

        Assert.Equal(customerId, response.CustomerId);
        Assert.Equal(catalogEventId, response.CatalogEventId);
        Assert.Equal(seatIds, response.SeatIds);
        Assert.Equal(500_000, response.TotalAmount);
        Assert.Equal("VND", response.Currency);
        Assert.Equal(nameof(OrderStatus.Pending), response.Status);

        // Exactly one event staged for a brand-new stream.
        var events = store.EventsFor(response.OrderId);
        var placed = Assert.Single(events);
        Assert.IsType<OrderPlaced>(placed);

        // The saga-starting integration event went out alongside the stream write.
        var published = Assert.Single(publisher.Published);
        var orderSubmitted = Assert.IsType<OrderSubmitted>(published);
        Assert.Equal(response.OrderId, orderSubmitted.OrderId);
        Assert.Equal("Nguyen Van A", orderSubmitted.CustomerName);
    }

    [Fact]
    public async Task HandleAsync_NoSeats_Throws()
    {
        var handler = new PlaceOrderCommandHandler(new FakeOrderStore(), new FakeIntegrationEventPublisher(), TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(ValidCommand(seatIds: [], totalAmount: 100_000)));
    }

    [Fact]
    public async Task HandleAsync_NoCurrency_DefaultsToVnd()
    {
        var handler = new PlaceOrderCommandHandler(new FakeOrderStore(), new FakeIntegrationEventPublisher(), TimeProvider.System);

        var response = await handler.HandleAsync(ValidCommand(seatIds: [Guid.NewGuid()], totalAmount: 100_000, currency: null));

        Assert.Equal("VND", response.Currency);
    }
}
