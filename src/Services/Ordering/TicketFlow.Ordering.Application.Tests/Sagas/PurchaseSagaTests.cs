using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Sagas;

namespace TicketFlow.Ordering.Application.Tests.Sagas;

/// <summary>
/// In-memory MassTransit test harness — no RabbitMQ, no Marten, no real participants. Verifies
/// the saga's own transition logic (what it publishes, what state it moves to) in isolation.
/// The real-RabbitMQ + real-Marten-persistence side of Commit 4 is verified separately via
/// Docker (see docs/phase-5-plan.md Commit 4 section).
/// </summary>
public sealed class PurchaseSagaTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private FakeSaleRecordedPublisher _saleRecorded = null!;

    public async Task InitializeAsync()
    {
        _saleRecorded = new FakeSaleRecordedPublisher();

        _provider = new ServiceCollection()
            // Kafka Rider needs a real broker (Commit 6) — no in-memory analog exists, so
            // PurchaseSaga's ISaleRecordedPublisher dependency (the Application-layer port, not
            // MassTransit.Kafka's ITopicProducer directly — see that interface's doc comment) is
            // faked here instead of wired through AddRider/UsingKafka, same "no RabbitMQ, no
            // Marten" spirit as the rest of this harness. The fake CAPTURES what the saga records
            // so tests can assert on it; Produce() to Kafka itself is verified for real via Docker.
            .AddSingleton<ISaleRecordedPublisher>(_saleRecorded)
            .AddMassTransitTestHarness(cfg => cfg.AddSagaStateMachine<PurchaseSaga, PurchaseState>())
            .BuildServiceProvider(true);

        _harness = _provider.GetTestHarness();
        await _harness.Start();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Happy_path_drives_saga_from_OrderSubmitted_to_finalized()
    {
        var orderId = Guid.NewGuid();
        var catalogEventId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var seatIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, catalogEventId, Guid.NewGuid(), holdId, seatIds, 250.00m, "CreditCard",
            "Nguyen Van A", "a@example.com", DateTimeOffset.UtcNow));

        Assert.True(await _harness.Published.Any<ConfirmSeats>(x => x.Context.Message.OrderId == orderId));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingSeats));

        await _harness.Bus.Publish(new SeatsConfirmed(orderId, seatIds, DateTimeOffset.UtcNow));

        Assert.True(await _harness.Published.Any<ProcessPayment>(x => x.Context.Message.OrderId == orderId));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingPayment));

        var paymentId = Guid.NewGuid();
        await _harness.Bus.Publish(new PaymentProcessed(orderId, paymentId, 250.00m, DateTimeOffset.UtcNow));

        Assert.True(await _harness.Published.Any<IssueTickets>(x =>
            x.Context.Message.OrderId == orderId
            && x.Context.Message.EventId == catalogEventId
            && x.Context.Message.CustomerEmail == "a@example.com"));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingTickets));

        var pdfUrls = new List<string> { "tickets/order/seat1.pdf", "tickets/order/seat2.pdf" };
        await _harness.Bus.Publish(new TicketsIssued(orderId, pdfUrls, DateTimeOffset.UtcNow));

        Assert.True(await _harness.Published.Any<OrderCompleted>(x => x.Context.Message.OrderId == orderId));
        Assert.True(await _harness.Published.Any<NotifyOrderCompleted>(x => x.Context.Message.OrderId == orderId));

        // SetCompletedWhenFinalized() removes the instance — no longer found in any known state.
        Assert.Null(await sagaHarness.NotExists(orderId));

        // The analytics fact (Kafka, Commit 6): exactly one SaleRecorded for this completed order,
        // fields mapped correctly from saga state. Also locks in that the .ThenAsync(...) publish
        // actually runs on the happy path — the bug where placing it right before .Finalize() made
        // it silently skip would surface here as zero captured records.
        var sale = Assert.Single(_saleRecorded.Published);
        Assert.Equal(orderId, sale.OrderId);
        Assert.Equal(catalogEventId, sale.EventId);
        Assert.Equal(seatIds.Count, sale.SeatCount);
        Assert.Equal(250.00m, sale.Amount);
    }

    [Fact]
    public async Task Partially_confirmed_hold_releases_confirmed_seats_and_cancels_without_charging()
    {
        var orderId = Guid.NewGuid();
        var heldSeat = Guid.NewGuid();
        var lostSeat = Guid.NewGuid();
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        // The order lists two seats; one of them is no longer owned by the hold (expired, or never
        // part of it), so reservation confirms only the other one.
        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [heldSeat, lostSeat], 200m, "CreditCard",
            "D", "d@example.com", DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingSeats));

        await _harness.Bus.Publish(new SeatsConfirmed(orderId, [heldSeat], DateTimeOffset.UtcNow));

        // The amount was priced for both seats, so charging it would overcharge: release what was
        // confirmed (and only that, ReleaseSeats un-sells every listed seat) and cancel instead.
        Assert.True(await _harness.Published.Any<ReleaseSeats>(x => x.Context.Message.OrderId == orderId));
        var release = _harness.Published.Select<ReleaseSeats>(x => x.Context.Message.OrderId == orderId).Single();
        Assert.Equal([heldSeat], release.Context.Message.SeatIds);
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.CompensatingSeats));

        await _harness.Bus.Publish(new SeatsReleased(orderId, release.Context.Message.HoldId, DateTimeOffset.UtcNow));

        // Wait for the saga to finish before asserting on what it published last.
        Assert.Null(await sagaHarness.NotExists(orderId));
        Assert.True(await _harness.Published.Any<OrderCancelledIntegrationEvent>(x => x.Context.Message.OrderId == orderId));

        // Negative check last: it waits out the harness inactivity timeout.
        Assert.False(await _harness.Published.Any<ProcessPayment>(x => x.Context.Message.OrderId == orderId));
    }

    [Fact]
    public async Task Order_listing_cheaper_seats_than_the_hold_is_cancelled_without_charging()
    {
        var orderId = Guid.NewGuid();
        var cheapSeatInRequest = Guid.NewGuid();
        var vipSeat1 = Guid.NewGuid();
        var vipSeat2 = Guid.NewGuid();
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        // Priced for the one cheap seat the request lists, while the hold actually owns two VIP seats.
        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [cheapSeatInRequest], 300_000m, "CreditCard",
            "F", "f@example.com", DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingSeats));

        await _harness.Bus.Publish(new SeatsConfirmed(orderId, [vipSeat1, vipSeat2], DateTimeOffset.UtcNow));

        Assert.True(await _harness.Published.Any<ReleaseSeats>(x => x.Context.Message.OrderId == orderId));
        var release = _harness.Published.Select<ReleaseSeats>(x => x.Context.Message.OrderId == orderId).Single();
        Assert.Equal(new[] { vipSeat1, vipSeat2 }.Order(), release.Context.Message.SeatIds.Order());
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.CompensatingSeats));

        // Negative check last: it waits out the harness inactivity timeout.
        Assert.False(await _harness.Published.Any<ProcessPayment>(x => x.Context.Message.OrderId == orderId));
    }

    [Fact]
    public void Instance_persisted_before_RequestedSeatIds_existed_still_compares_against_its_requested_seats()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var resumed = new PurchaseState { SeatIds = [first, second] };

        resumed.RecordConfirmedSeats([second, first]);

        Assert.True(resumed.ConfirmedAllRequestedSeats());
        Assert.Equal(new[] { first, second }.Order(), resumed.SeatIds.Order());
    }

    [Fact]
    public async Task Fully_confirmed_hold_in_any_order_proceeds_to_payment_and_tickets()
    {
        var orderId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [first, second], 200m, "CreditCard",
            "E", "e@example.com", DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingSeats));

        // Redis SMEMBERS gives no ordering guarantee, so the confirmed list may come back reordered.
        await _harness.Bus.Publish(new SeatsConfirmed(orderId, [second, first], DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingPayment));
        await _harness.Bus.Publish(new PaymentProcessed(orderId, Guid.NewGuid(), 200m, DateTimeOffset.UtcNow));

        Assert.True(await _harness.Published.Any<IssueTickets>(x => x.Context.Message.OrderId == orderId));
        var issue = _harness.Published.Select<IssueTickets>(x => x.Context.Message.OrderId == orderId).Single();
        Assert.Equal(new[] { first, second }.Order(), issue.Context.Message.SeatIds.Order());
    }

    [Fact]
    public async Task SeatsConfirmationFailed_cancels_directly_no_compensation_needed()
    {
        var orderId = Guid.NewGuid();
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], 100m, "CreditCard",
            "A", "a@example.com", DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingSeats));

        await _harness.Bus.Publish(new SeatsConfirmationFailed(orderId, Guid.NewGuid(), "Hold expired."));

        Assert.True(await _harness.Published.Any<OrderCancelledIntegrationEvent>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.Reason == "Hold expired."));

        // No compensation commands — nothing was ever charged or issued.
        Assert.False(await _harness.Published.Any<ReleaseSeats>(x => x.Context.Message.OrderId == orderId));
        Assert.False(await _harness.Published.Any<RefundPayment>(x => x.Context.Message.OrderId == orderId));
        Assert.Null(await sagaHarness.NotExists(orderId));
    }

    [Fact]
    public async Task PaymentFailed_releases_seats_then_cancels()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), holdId, [seatId], 100m, "CreditCard",
            "B", "b@example.com", DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingSeats));
        await _harness.Bus.Publish(new SeatsConfirmed(orderId, [seatId], DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingPayment));

        await _harness.Bus.Publish(new PaymentFailed(orderId, "Gateway declined the charge."));

        Assert.True(await _harness.Published.Any<ReleaseSeats>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.HoldId == holdId));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.CompensatingSeats));

        await _harness.Bus.Publish(new SeatsReleased(orderId, holdId, DateTimeOffset.UtcNow));

        // Wait for the saga to finish before asserting on what it published last.
        Assert.Null(await sagaHarness.NotExists(orderId));
        Assert.True(await _harness.Published.Any<OrderCancelledIntegrationEvent>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.Reason == "Gateway declined the charge."));
    }

    [Fact]
    public async Task TicketingFailed_refunds_then_releases_seats_then_cancels()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), holdId, [seatId], 100m, "CreditCard",
            "C", "c@example.com", DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingSeats));
        await _harness.Bus.Publish(new SeatsConfirmed(orderId, [seatId], DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingPayment));
        await _harness.Bus.Publish(new PaymentProcessed(orderId, paymentId, 100m, DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingTickets));

        await _harness.Bus.Publish(new TicketingFailed(orderId, "MinIO unreachable."));

        Assert.True(await _harness.Published.Any<RefundPayment>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.PaymentId == paymentId));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.CompensatingRefund));

        await _harness.Bus.Publish(new PaymentRefunded(orderId, paymentId, 100m, DateTimeOffset.UtcNow));

        Assert.True(await _harness.Published.Any<ReleaseSeats>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.HoldId == holdId));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.CompensatingSeats));

        await _harness.Bus.Publish(new SeatsReleased(orderId, holdId, DateTimeOffset.UtcNow));

        // Wait for the saga to finish before asserting on what it published last.
        Assert.Null(await sagaHarness.NotExists(orderId));
        Assert.True(await _harness.Published.Any<OrderCancelledIntegrationEvent>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.Reason == "MinIO unreachable."));

        // The real invariant: an order that was charged then refunded must NOT count as a sale.
        // SaleRecorded lives only on the TicketsIssued -> completion path, never on any compensation
        // branch — so analytics revenue never double-counts a reversed transaction. This is the
        // failure mode most likely to slip through (payment WAS processed here before ticketing
        // failed), which is why the negative assertion lands on this branch specifically.
        Assert.Empty(_saleRecorded.Published);
    }
}

/// <summary>
/// Captures what the saga publishes to the analytics (Kafka) stream so tests can assert on it —
/// the real KafkaSaleRecordedPublisher (Ordering.Api) is exercised via Docker instead (Commit 6).
/// ConcurrentBag because MassTransit consumes on background threads; a plain List could tear.
/// </summary>
public sealed class FakeSaleRecordedPublisher : ISaleRecordedPublisher
{
    public System.Collections.Concurrent.ConcurrentBag<SaleRecorded> Published { get; } = [];

    public Task PublishAsync(SaleRecorded message, CancellationToken cancellationToken = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
