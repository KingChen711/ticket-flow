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

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            // Kafka Rider needs a real broker (Commit 6) — no in-memory analog exists, so
            // PurchaseSaga's ISaleRecordedPublisher dependency (the Application-layer port, not
            // MassTransit.Kafka's ITopicProducer directly — see that interface's doc comment) is
            // faked here instead of wired through AddRider/UsingKafka, same "no RabbitMQ, no
            // Marten" spirit as the rest of this harness. Produce() to Kafka itself is verified
            // for real via Docker.
            .AddSingleton<ISaleRecordedPublisher, FakeSaleRecordedPublisher>()
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
        Assert.Null(await sagaHarness.Exists(orderId, x => x.AwaitingTickets));
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
        Assert.Null(await sagaHarness.Exists(orderId, x => x.AwaitingSeats));
    }

    [Fact]
    public async Task PaymentFailed_releases_seats_then_cancels()
    {
        var orderId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), holdId, [Guid.NewGuid()], 100m, "CreditCard",
            "B", "b@example.com", DateTimeOffset.UtcNow));
        await _harness.Bus.Publish(new SeatsConfirmed(orderId, [Guid.NewGuid()], DateTimeOffset.UtcNow));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.AwaitingPayment));

        await _harness.Bus.Publish(new PaymentFailed(orderId, "Gateway declined the charge."));

        Assert.True(await _harness.Published.Any<ReleaseSeats>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.HoldId == holdId));
        Assert.NotNull(await sagaHarness.Exists(orderId, x => x.CompensatingSeats));

        await _harness.Bus.Publish(new SeatsReleased(orderId, holdId, DateTimeOffset.UtcNow));

        Assert.True(await _harness.Published.Any<OrderCancelledIntegrationEvent>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.Reason == "Gateway declined the charge."));
        Assert.Null(await sagaHarness.Exists(orderId, x => x.CompensatingSeats));
    }

    [Fact]
    public async Task TicketingFailed_refunds_then_releases_seats_then_cancels()
    {
        var orderId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var sagaHarness = _harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();

        await _harness.Bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), Guid.NewGuid(), holdId, [Guid.NewGuid()], 100m, "CreditCard",
            "C", "c@example.com", DateTimeOffset.UtcNow));
        await _harness.Bus.Publish(new SeatsConfirmed(orderId, [Guid.NewGuid()], DateTimeOffset.UtcNow));
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

        Assert.True(await _harness.Published.Any<OrderCancelledIntegrationEvent>(x =>
            x.Context.Message.OrderId == orderId && x.Context.Message.Reason == "MinIO unreachable."));
        Assert.Null(await sagaHarness.Exists(orderId, x => x.CompensatingSeats));
    }
}

public sealed class FakeSaleRecordedPublisher : ISaleRecordedPublisher
{
    public Task PublishAsync(SaleRecorded message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
