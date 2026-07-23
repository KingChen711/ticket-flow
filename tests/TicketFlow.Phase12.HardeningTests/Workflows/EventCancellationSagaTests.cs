using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Contracts.Messaging.Workflows;
using TicketFlow.Ordering.Application.Sagas;

namespace TicketFlow.Phase12.HardeningTests.Workflows;

public sealed class EventCancellationSagaTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Hold_only_order_releases_once_without_touching_payment()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new OrderFixture();
        var workflowId = Guid.NewGuid();

        await harness.Bus.Publish(order.Submitted());
        await harness.Bus.Publish(order.Cancel(workflowId));

        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "CompensatingSeats");

        await harness.Bus.Publish(new SeatsReleased(order.OrderId, order.HoldId, DateTimeOffset.UtcNow));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "Cancelled");

        Assert.Empty(harness.Published.Select<CancelPaymentForEvent>());
        Assert.Single(harness.Published.Select<ReleaseSeats>());
        Assert.Equal(
            workflowId,
            Assert.Single(harness.Published.Select<OrderCancelledIntegrationEvent>())
                .Context.Message.CancellationWorkflowId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task In_flight_payment_is_tombstoned_before_seats_are_released()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new OrderFixture();
        var workflowId = Guid.NewGuid();

        await harness.Bus.Publish(order.Submitted());
        await harness.Bus.Publish(new SeatsConfirmed(
            order.OrderId,
            [order.SeatId],
            DateTimeOffset.UtcNow));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "AwaitingPayment");

        await harness.Bus.Publish(order.Cancel(workflowId));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "CompensatingRefund");

        await harness.Bus.Publish(new PaymentStoppedForEvent(
            order.OrderId,
            workflowId,
            null,
            false,
            DateTimeOffset.UtcNow,
            $"event-cancel:{workflowId:N}:{order.OrderId:N}:payment"));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "CompensatingSeats");

        await harness.Bus.Publish(new SeatsReleased(order.OrderId, order.HoldId, DateTimeOffset.UtcNow));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "Cancelled");

        Assert.Single(harness.Published.Select<CancelPaymentForEvent>());
        Assert.Single(harness.Published.Select<ReleaseSeats>());
        Assert.Single(harness.Published.Select<OrderCancelledIntegrationEvent>());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Paid_ticketed_order_refunds_releases_and_emits_revocation_notification_fact_once()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new OrderFixture();
        var workflowId = Guid.NewGuid();

        await harness.Bus.Publish(order.Submitted());
        await harness.Bus.Publish(new SeatsConfirmed(order.OrderId, [order.SeatId], DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new PaymentProcessed(
            order.OrderId, order.PaymentId, order.Amount, DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new TicketsIssued(
            order.OrderId, [$"{order.OrderId}/{order.SeatId}.pdf"], DateTimeOffset.UtcNow));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "Completed");

        await harness.Bus.Publish(order.Cancel(workflowId));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "CompensatingRefund");

        await harness.Bus.Publish(new PaymentStoppedForEvent(
            order.OrderId,
            workflowId,
            order.PaymentId,
            true,
            DateTimeOffset.UtcNow,
            $"event-cancel:{workflowId:N}:{order.OrderId:N}:payment"));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "CompensatingSeats");

        await harness.Bus.Publish(new SeatsReleased(order.OrderId, order.HoldId, DateTimeOffset.UtcNow));
        await WaitForSagaAsync(harness, order.OrderId, state => state.CurrentState == "Cancelled");

        Assert.Single(harness.Published.Select<CancelPaymentForEvent>());
        Assert.Single(harness.Published.Select<ReleaseSeats>());
        var cancelled = Assert.Single(harness.Published.Select<OrderCancelledIntegrationEvent>()).Context.Message;
        Assert.Equal(workflowId, cancelled.CancellationWorkflowId);
        Assert.Equal(order.EventId, cancelled.CatalogEventId);
        Assert.Equal("buyer@example.com", cancelled.CustomerEmail);
    }

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(configuration =>
            {
                configuration.AddDelayedMessageScheduler();
                configuration.AddSagaStateMachine<PurchaseSaga, PurchaseState, PurchaseSagaDefinition>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(validateScopes: true);

    private static async Task WaitForSagaAsync(
        ITestHarness harness,
        Guid orderId,
        Func<PurchaseState, bool> predicate)
    {
        var saga = harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var instance = saga.Sagas.Contains(orderId);
            if (instance is not null && predicate(instance))
                return;

            await Task.Delay(10);
        }

        throw new TimeoutException(Describe(harness, orderId));
    }

    private static string Describe(ITestHarness harness, Guid orderId)
    {
        var saga = harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>().Sagas.Contains(orderId);
        var faults = harness.Published.Select<Fault>().Select(item => item.Context.Message.Exceptions.FirstOrDefault()?.Message);
        return saga is null
            ? $"Saga missing. Faults: {string.Join(" | ", faults)}"
            : $"State={saga.CurrentState}; workflow={saga.EventCancellationWorkflowId}; stop={saga.PaymentStoppedAt}; " +
              $"released={saga.SeatsReleasedAt}; cancelled={saga.CancelledAt}; " +
              $"consumed-stop={harness.Consumed.Select<PaymentStoppedForEvent>().Count()}; " +
              $"consumed-release={harness.Consumed.Select<SeatsReleased>().Count()}; faults={string.Join(" | ", faults)}";
    }

    private sealed class OrderFixture
    {
        public Guid OrderId { get; } = Guid.NewGuid();
        public Guid EventId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid HoldId { get; } = Guid.NewGuid();
        public Guid SeatId { get; } = Guid.NewGuid();
        public Guid PaymentId { get; } = Guid.NewGuid();
        public decimal Amount { get; } = 500_000m;

        public OrderSubmitted Submitted() => new(
            OrderId,
            EventId,
            UserId,
            HoldId,
            [SeatId],
            Amount,
            "CreditCard",
            "Buyer",
            "buyer@example.com",
            DateTimeOffset.UtcNow,
            "VND",
            7,
            $"checkout:{OrderId:N}",
            null,
            11);

        public CancelOrderForEvent Cancel(Guid workflowId) => new(
            workflowId,
            EventId,
            OrderId,
            "Event cancelled.",
            DateTimeOffset.UtcNow);
    }
}
