using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;
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
}
