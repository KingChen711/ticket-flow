using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Domain.Tests.Orders;

// No DB/Marten in these tests — Order.Load folds a hand-built event list, proving the same
// state-rebuild behaviour Marten's AggregateStreamAsync relies on, without spinning up Postgres
// (that's Commit 2's job).
public class OrderTests
{
    [Fact]
    public void Place_WithNoSeats_Throws()
    {
        var total = Money.Create(100_000);

        Assert.Throws<ArgumentException>(() =>
            Order.Place(Guid.NewGuid(), Guid.NewGuid(), [], total, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Place_ThenLoad_YieldsPendingOrderWithMatchingData()
    {
        var customerId = Guid.NewGuid();
        var catalogEventId = Guid.NewGuid();
        var seatIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var total = Money.Create(500_000, "VND");
        var now = DateTimeOffset.UtcNow;

        var placed = Order.Place(customerId, catalogEventId, seatIds, total, now);
        var order = Order.Load([placed]);

        Assert.Equal(placed.OrderId, order.Id);
        Assert.Equal(customerId, order.CustomerId);
        Assert.Equal(catalogEventId, order.CatalogEventId);
        Assert.Equal(seatIds, order.SeatIds);
        Assert.Equal(total, order.Total);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void LoadPlacedThenSeatsConfirmedThenPaymentAuthorizedThenTicketsIssued_YieldsTicketsIssuedStatus()
    {
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Money.Create(100_000), DateTimeOffset.UtcNow);
        var seatsConfirmed = new OrderSeatsConfirmed(placed.OrderId, DateTimeOffset.UtcNow);
        var paymentAuthorized = new OrderPaymentAuthorized(placed.OrderId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ticketsIssued = new OrderTicketsIssued(placed.OrderId, ["tickets/o/1.pdf"], DateTimeOffset.UtcNow);

        var order = Order.Load([placed, seatsConfirmed, paymentAuthorized, ticketsIssued]);

        Assert.Equal(OrderStatus.TicketsIssued, order.Status);
    }

    [Fact]
    public void SeatsConfirmed_ReplacesRequestedSeatsWithTheConfirmedOnes()
    {
        var kept = Guid.NewGuid();
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [kept, Guid.NewGuid()], Money.Create(200_000), DateTimeOffset.UtcNow);
        var seatsConfirmed = new OrderSeatsConfirmed(placed.OrderId, DateTimeOffset.UtcNow, [kept]);

        var order = Order.Load([placed, seatsConfirmed]);

        Assert.Equal([kept], order.SeatIds);
    }

    [Fact]
    public void SeatsConfirmed_StoredBeforeSeatIdsExisted_KeepsTheRequestedSeats()
    {
        var seatIds = new List<Guid> { Guid.NewGuid() };
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), seatIds, Money.Create(100_000), DateTimeOffset.UtcNow);

        var order = Order.Load([placed, new OrderSeatsConfirmed(placed.OrderId, DateTimeOffset.UtcNow)]);

        Assert.Equal(seatIds, order.SeatIds);
    }

    [Fact]
    public void LoadPlacedThenConfirmed_YieldsConfirmedStatus()
    {
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Money.Create(100_000), DateTimeOffset.UtcNow);
        var confirmed = new OrderConfirmed(placed.OrderId, DateTimeOffset.UtcNow);

        var order = Order.Load([placed, confirmed]);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void LoadPlacedThenCancelled_YieldsCancelledStatus()
    {
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Money.Create(100_000), DateTimeOffset.UtcNow);
        var cancelled = new OrderCancelled(placed.OrderId, "customer changed mind", DateTimeOffset.UtcNow);

        var order = Order.Load([placed, cancelled]);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void Confirm_OnPendingOrder_ReturnsEventWithoutMutating()
    {
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Money.Create(100_000), DateTimeOffset.UtcNow);
        var order = Order.Load([placed]);

        var confirmed = order.Confirm(DateTimeOffset.UtcNow);

        Assert.Equal(placed.OrderId, confirmed.OrderId);
        // Confirm() is a pure decision — it must NOT mutate `order` itself, only Apply does.
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void Confirm_OnAlreadyConfirmedOrder_Throws()
    {
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Money.Create(100_000), DateTimeOffset.UtcNow);
        var confirmed = new OrderConfirmed(placed.OrderId, DateTimeOffset.UtcNow);
        var order = Order.Load([placed, confirmed]);

        Assert.Throws<InvalidOperationException>(() => order.Confirm(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cancel_OnAlreadyCancelledOrder_Throws()
    {
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Money.Create(100_000), DateTimeOffset.UtcNow);
        var cancelled = new OrderCancelled(placed.OrderId, "reason", DateTimeOffset.UtcNow);
        var order = Order.Load([placed, cancelled]);

        Assert.Throws<InvalidOperationException>(() => order.Cancel("again", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cancel_OnConfirmedOrder_Throws()
    {
        var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Money.Create(100_000), DateTimeOffset.UtcNow);
        var confirmed = new OrderConfirmed(placed.OrderId, DateTimeOffset.UtcNow);
        var order = Order.Load([placed, confirmed]);

        Assert.Throws<InvalidOperationException>(() => order.Cancel("too late", DateTimeOffset.UtcNow));
    }
}
