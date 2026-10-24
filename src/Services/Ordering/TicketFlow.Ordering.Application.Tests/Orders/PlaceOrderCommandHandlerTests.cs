using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Tests.Orders;

public class PlaceOrderCommandHandlerTests
{
    private static PlaceOrderCommand ValidCommand(
        Guid? customerId = null, Guid? catalogEventId = null, IReadOnlyList<Guid>? seatIds = null) =>
        new(
            customerId ?? Guid.NewGuid(),
            catalogEventId ?? Guid.NewGuid(),
            seatIds ?? [Guid.NewGuid(), Guid.NewGuid()],
            Guid.NewGuid(),
            "CreditCard",
            "Nguyen Van A",
            "a@example.com");

    private static PlaceOrderCommandHandler Handler(ISeatPricingSource pricing, FakeOrderStore? store = null, FakeIntegrationEventPublisher? publisher = null) =>
        new(store ?? new FakeOrderStore(), publisher ?? new FakeIntegrationEventPublisher(), pricing, TimeProvider.System);

    [Fact]
    public async Task HandleAsync_ValidCommand_StartsStreamAndReturnsPendingOrder()
    {
        var store = new FakeOrderStore();
        var publisher = new FakeIntegrationEventPublisher();
        var customerId = Guid.NewGuid();
        var catalogEventId = Guid.NewGuid();
        var seatIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var handler = Handler(FakeSeatPricingSource.ForSeats(seatIds, 250_000), store, publisher);

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

        // The saga-starting integration event went out alongside the stream write, carrying the
        // server-computed amount that payment will charge.
        var published = Assert.Single(publisher.Published);
        var orderSubmitted = Assert.IsType<OrderSubmitted>(published);
        Assert.Equal(response.OrderId, orderSubmitted.OrderId);
        Assert.Equal("Nguyen Van A", orderSubmitted.CustomerName);
        Assert.Equal(500_000, orderSubmitted.Amount);
    }

    [Fact]
    public async Task HandleAsync_SumsEachSeatAtItsOwnSectionPrice()
    {
        var vip = Guid.NewGuid();
        var standard = Guid.NewGuid();
        var vipSeat = Guid.NewGuid();
        var standardSeat1 = Guid.NewGuid();
        var standardSeat2 = Guid.NewGuid();
        var pricing = new FakeSeatPricingSource();
        pricing.SectionPrices.Add(new SectionPrice(vip, 1_000_000, "VND"));
        pricing.SectionPrices.Add(new SectionPrice(standard, 300_000, "VND"));
        pricing.SeatSections[vipSeat] = vip;
        pricing.SeatSections[standardSeat1] = standard;
        pricing.SeatSections[standardSeat2] = standard;

        var response = await Handler(pricing).HandleAsync(ValidCommand(seatIds: [vipSeat, standardSeat1, standardSeat2]));

        Assert.Equal(1_600_000, response.TotalAmount);
    }

    [Fact]
    public async Task HandleAsync_SeatOutsideTheEvent_IsRejectedAndNothingIsWritten()
    {
        var known = Guid.NewGuid();
        var store = new FakeOrderStore();
        var publisher = new FakeIntegrationEventPublisher();
        var handler = Handler(FakeSeatPricingSource.ForSeats([known]), store, publisher);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(ValidCommand(seatIds: [known, Guid.NewGuid()])));

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task HandleAsync_SectionWithoutPriceTier_IsRejected()
    {
        var seat = Guid.NewGuid();
        var pricing = new FakeSeatPricingSource();
        pricing.SeatSections[seat] = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(pricing).HandleAsync(ValidCommand(seatIds: [seat])));
    }

    [Fact]
    public async Task HandleAsync_MixedCurrencies_IsRejected()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var sectionA = Guid.NewGuid();
        var sectionB = Guid.NewGuid();
        var pricing = new FakeSeatPricingSource();
        pricing.SectionPrices.Add(new SectionPrice(sectionA, 100, "VND"));
        pricing.SectionPrices.Add(new SectionPrice(sectionB, 5, "USD"));
        pricing.SeatSections[a] = sectionA;
        pricing.SeatSections[b] = sectionB;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(pricing).HandleAsync(ValidCommand(seatIds: [a, b])));
    }

    [Fact]
    public async Task HandleAsync_SameSeatTwice_IsRejectedInsteadOfChargedTwice()
    {
        var seat = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Handler(FakeSeatPricingSource.ForSeats([seat])).HandleAsync(ValidCommand(seatIds: [seat, seat])));
    }

    [Fact]
    public async Task HandleAsync_NoSeats_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Handler(new FakeSeatPricingSource()).HandleAsync(ValidCommand(seatIds: [])));
    }
}
