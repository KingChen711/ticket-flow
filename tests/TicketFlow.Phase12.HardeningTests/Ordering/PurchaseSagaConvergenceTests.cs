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
using TicketFlow.Phase12.HardeningTests.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Ordering;

/// <summary>
/// The same convergence property as the RabbitMQ permutation suite, on the in-memory harness.
/// Deterministic and fast, so a failure points at the state machine itself rather than at broker
/// timing — the RabbitMQ suite then proves the same thing survives a real transport.
/// </summary>
public sealed class PurchaseSagaConvergenceTests
{
    public static TheoryData<string[]> Permutations()
    {
        var data = new TheoryData<string[]>();
        var milestones = new[] { "SeatsConfirmed", "PaymentProcessed", "TicketsIssued" };
        foreach (var first in milestones)
        foreach (var second in milestones.Where(item => item != first))
        foreach (var third in milestones.Where(item => item != first && item != second))
            data.Add([first, second, third]);

        return data;
    }

    [Theory]
    [MemberData(nameof(Permutations))]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Every_delivery_order_with_duplicates_must_complete_the_order_exactly_once(string[] deliveryOrder)
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var order = new OrderFixture();
        await harness.Bus.Publish(order.Submitted());
        Assert.True(await harness.Published.Any<ConfirmSeats>());

        foreach (var milestone in deliveryOrder)
        {
            // Two distinct envelopes per business fact: MessageId dedup cannot help, the state
            // machine has to make the transition itself idempotent.
            await Publish(harness, milestone, order);
            await Publish(harness, milestone, order);
        }

        Assert.True(
            await harness.Published.Any<OrderCompleted>(x => x.Context.Message.OrderId == order.OrderId),
            $"Delivery [{string.Join(", ", deliveryOrder)}] never completed.");

        // Completion is exactly once — that is the business fact.
        Assert.Equal(1, harness.Published.Select<OrderCompleted>().Count());
        Assert.Equal(1, harness.Published.Select<NotifyOrderCompleted>().Count());

        // The participant commands are at most once, NOT exactly once, and the difference is the
        // whole point: if TicketsIssued arrives before payment is authorized, the tickets already
        // exist and asking for them again would be the bug. An earlier version of this test
        // asserted `== 1` here and failed five of six permutations while the saga was behaving
        // correctly — the assertion, not the machine, was wrong.
        Assert.InRange(harness.Published.Select<ProcessPayment>().Count(), 0, 1);
        Assert.InRange(harness.Published.Select<IssueTickets>().Count(), 0, 1);
    }

    /// <summary>
    /// Commit 9's scope, not Commit 8's: the plan assigns "missing-instance response được
    /// scheduled redelivery thay vì drop" there. Commit 8 makes a milestone order-independent once
    /// an instance exists; a milestone that arrives before the instance is created still matches no
    /// correlation and is dropped, because only <c>Initially</c> may create one. Left red on
    /// purpose so the gap stays visible instead of being rediscovered later.
    /// </summary>
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task A_milestone_arriving_before_the_saga_exists_must_not_be_lost()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new OrderFixture();

        // The participant answered before the order stream's own submit reached the saga.
        await harness.Bus.Publish(new SeatsConfirmed(order.OrderId, [order.SeatId], DateTimeOffset.UtcNow));
        await harness.Bus.Publish(order.Submitted());
        await Publish(harness, "PaymentProcessed", order);
        await Publish(harness, "TicketsIssued", order);

        Assert.True(await harness.Published.Any<OrderCompleted>(x => x.Context.Message.OrderId == order.OrderId));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_duplicate_OrderSubmitted_must_not_start_the_purchase_twice()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new OrderFixture();

        // Commit 7's outbox relay is at-least-once, so this is a real delivery pattern.
        await harness.Bus.Publish(order.Submitted());
        Assert.True(await harness.Published.Any<ConfirmSeats>());
        await harness.Bus.Publish(order.Submitted());
        await harness.InactivityTask;

        Assert.Equal(1, harness.Published.Select<ConfirmSeats>().Count());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_late_milestone_after_completion_must_not_reopen_the_order()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new OrderFixture();

        await harness.Bus.Publish(order.Submitted());
        await Publish(harness, "SeatsConfirmed", order);
        await Publish(harness, "PaymentProcessed", order);
        await Publish(harness, "TicketsIssued", order);
        Assert.True(await harness.Published.Any<OrderCompleted>());

        // A ticketing failure that lost a race with the success must not cancel a completed order.
        await harness.Bus.Publish(new TicketingFailed(order.OrderId, "Late failure."));
        await harness.InactivityTask;

        Assert.Equal(0, harness.Published.Select<RefundPayment>().Count());
        Assert.Equal(0, harness.Published.Select<OrderCancelledIntegrationEvent>().Count());
        Assert.Equal(1, harness.Published.Select<OrderCompleted>().Count());
    }


    private static Task Publish(ITestHarness harness, string milestone, OrderFixture order) =>
        milestone switch
        {
            "SeatsConfirmed" => harness.Bus.Publish(
                new SeatsConfirmed(order.OrderId, [order.SeatId], DateTimeOffset.UtcNow)),
            "PaymentProcessed" => harness.Bus.Publish(
                new PaymentProcessed(order.OrderId, order.PaymentId, 500_000m, DateTimeOffset.UtcNow)),
            "TicketsIssued" => harness.Bus.Publish(
                new TicketsIssued(order.OrderId, [$"{order.OrderId}/{order.SeatId}.pdf"], DateTimeOffset.UtcNow)),
            _ => throw new ArgumentOutOfRangeException(nameof(milestone), milestone, "Unknown milestone."),
        };

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<ISaleRecordedPublisher, RecordingSaleRecordedPublisher>()
            .AddMassTransitTestHarness(configuration =>
                configuration.AddSagaStateMachine<PurchaseSaga, PurchaseState>().InMemoryRepository())
            .BuildServiceProvider(validateScopes: true);

    private sealed class OrderFixture
    {
        public Guid OrderId { get; } = Guid.NewGuid();
        public Guid SeatId { get; } = Guid.NewGuid();
        public Guid PaymentId { get; } = Guid.NewGuid();

        public OrderSubmitted Submitted() => new(
            OrderId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            [SeatId],
            500_000m,
            "CreditCard",
            "Buyer",
            "buyer@example.com",
            DateTimeOffset.UtcNow);
    }
}
