using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Ordering.Application.Sagas;
using TicketFlow.Phase12.HardeningTests.Support;

namespace TicketFlow.Phase12.HardeningTests.Ordering;

/// <summary>
/// Commit 9's invariants: no wait is silent forever, no deadline invents an outcome, and an
/// operator can move a stuck order without asserting one.
/// </summary>
/// <remarks>
/// Deadlines are driven by publishing <see cref="PurchaseDeadlineExpired"/> directly rather than
/// by waiting on the real five-minute timer. What is under test is the decision the saga makes when
/// a deadline arrives — the scheduling itself is MassTransit's, and a test that slept for the real
/// delay would prove nothing extra while making the suite unusable.
/// </remarks>
public sealed class PurchaseSagaDeadlineTests
{
    /// <summary>
    /// Generous on purpose. These waits are for a decision the saga makes immediately, so a correct
    /// system satisfies them at once — the headroom only covers a machine running the whole suite
    /// at once. A tighter 5s made one of these fail in the full run while passing in isolation,
    /// which is a flaky test, not a finding.
    /// </summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_silent_participant_must_cause_the_command_to_be_re_sent()
    {
        var (harness, order) = await StartAwaitingPaymentAsync();
        Assert.Equal(1, harness.Published.Select<ProcessPayment>().Count());

        await FireDeadlineAsync(harness, order);

        // Re-sending is safe because ProcessPayment is idempotent (Commit 6); staying silent is not.
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => harness.Published.Select<ProcessPayment>().Count() == 2, SettleTimeout));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_stale_deadline_must_be_ignored_rather_than_counted_as_silence()
    {
        var (harness, order) = await StartAwaitingPaymentAsync();
        var state = await LoadAsync(harness, order.OrderId);
        var staleToken = state.DeadlineTokenId!.Value;

        // The participant answers, so the saga moves on and re-arms with a new token — but the old
        // deadline was already in flight and still arrives.
        await harness.Bus.Publish(new PaymentProcessed(order.OrderId, order.PaymentId, 500_000m, DateTimeOffset.UtcNow));
        Assert.True(await harness.Published.Any<IssueTickets>());

        await harness.Bus.Publish(new PurchaseDeadlineExpired(order.OrderId, "AwaitingPayment", staleToken));
        Assert.True(await harness.Consumed.Any<PurchaseDeadlineExpired>(
            x => x.Context.Message.Token == staleToken));

        var after = await LoadAsync(harness, order.OrderId);
        Assert.Equal(0, after.DeadlineStrikes);
        Assert.Equal(1, harness.Published.Select<ProcessPayment>().Count());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Exhausting_the_retries_must_escalate_to_manual_review_without_cancelling()
    {
        var (harness, order) = await StartAwaitingPaymentAsync();

        for (var attempt = 0; attempt <= PurchaseSaga.MaxDeadlineStrikes; attempt++)
            await FireDeadlineAsync(harness, order);

        Assert.True(await AsyncTest.WaitUntilAsync(
            () => LoadAsync(harness, order.OrderId).GetAwaiter().GetResult().ManualReviewAt is not null,
            SettleTimeout));

        // Cancelling on silence would release seats for a purchase that may in fact have been paid.
        // The order stops, visibly, and waits for a human instead.
        var state = await LoadAsync(harness, order.OrderId);
        Assert.Equal("ManualReview", state.CurrentState);
        Assert.Equal(0, harness.Published.Select<OrderCancelledIntegrationEvent>().Count());
        Assert.Equal(0, harness.Published.Select<ReleaseSeats>().Count());
        Assert.Equal(0, harness.Published.Select<OrderCompleted>().Count());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_manual_review_order_must_stop_arming_further_deadlines()
    {
        var (harness, order) = await StartAwaitingPaymentAsync();
        for (var attempt = 0; attempt <= PurchaseSaga.MaxDeadlineStrikes; attempt++)
            await FireDeadlineAsync(harness, order);
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => LoadAsync(harness, order.OrderId).GetAwaiter().GetResult().ManualReviewAt is not null,
            SettleTimeout));
        // Waited for, not sampled. The count is deterministic — the initial ProcessPayment plus one
        // re-send per strike, since the strike after MaxDeadlineStrikes escalates instead of
        // re-sending — but FireDeadlineAsync returns as soon as the saved strike count moves, and
        // the in-memory outbox releases that strike's re-send just after. Sampling here could read
        // the count before the last re-send lands and then see it arrive during the assert below,
        // which is what made this fail only in a full-suite run.
        var sentBefore = 1 + PurchaseSaga.MaxDeadlineStrikes;
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => harness.Published.Select<ProcessPayment>().Count() == sentBefore, SettleTimeout));

        await harness.Bus.Publish(new PurchaseDeadlineExpired(order.OrderId, "ManualReview", Guid.NewGuid()));
        // TestHarness.Any uses its short inactivity timeout (about one second). Under the full
        // container-heavy suite the message can legitimately enter the in-memory transport just
        // after that window even though the saga processes it correctly. Poll the same consumed
        // collection under this class's explicit settle budget, as the state/count assertions do.
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => harness.Consumed.Select<PurchaseDeadlineExpired>()
                .Any(x => x.Context.Message.ExpectedState == "ManualReview"),
            SettleTimeout));

        // "Không state vô hạn im lặng" cuts both ways: an escalated order must not keep retrying
        // a participant that has already ignored it MaxDeadlineStrikes times.
        Assert.Equal(sentBefore, harness.Published.Select<ProcessPayment>().Count());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task An_operator_resume_must_put_a_reviewed_order_back_under_automation()
    {
        var (harness, order) = await StartAwaitingPaymentAsync();
        for (var attempt = 0; attempt <= PurchaseSaga.MaxDeadlineStrikes; attempt++)
            await FireDeadlineAsync(harness, order);
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => LoadAsync(harness, order.OrderId).GetAwaiter().GetResult().ManualReviewAt is not null,
            SettleTimeout));
        // Same deterministic count, waited for rather than sampled, for the same reason as above.
        var sentBefore = 1 + PurchaseSaga.MaxDeadlineStrikes;
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => harness.Published.Select<ProcessPayment>().Count() == sentBefore, SettleTimeout));

        await harness.Bus.Publish(new ResumePurchase(order.OrderId, "operator-1"));
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => LoadAsync(harness, order.OrderId).GetAwaiter().GetResult().ManualReviewAt is null,
            SettleTimeout));
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => harness.Published.Select<ProcessPayment>().Count() == sentBefore + 1, SettleTimeout));

        var state = await LoadAsync(harness, order.OrderId);
        Assert.Null(state.ManualReviewAt);
        Assert.Equal(0, state.DeadlineStrikes);
        Assert.Equal(sentBefore + 1, harness.Published.Select<ProcessPayment>().Count());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Resume_must_not_reopen_an_order_that_already_finished()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new SagaOrderFixture();
        await harness.Bus.Publish(order.Submitted());
        await harness.Bus.Publish(new SeatsConfirmed(order.OrderId, [order.SeatId], DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new PaymentProcessed(order.OrderId, order.PaymentId, 500_000m, DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new TicketsIssued(order.OrderId, [$"{order.OrderId}.pdf"], DateTimeOffset.UtcNow));
        Assert.True(await harness.Published.Any<OrderCompleted>());

        await harness.Bus.Publish(new ResumePurchase(order.OrderId, "operator-1"));
        Assert.True(await harness.Consumed.Any<ResumePurchase>());

        // "Try again" on a finished order must not undo its outcome or duplicate it.
        Assert.Equal(1, harness.Published.Select<OrderCompleted>().Count());
        Assert.Equal("Completed", (await LoadAsync(harness, order.OrderId)).CurrentState);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Completing_an_order_must_hand_the_sale_to_a_relay_instead_of_producing_inline()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new SagaOrderFixture();
        await harness.Bus.Publish(order.Submitted());
        await harness.Bus.Publish(new SeatsConfirmed(order.OrderId, [order.SeatId], DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new PaymentProcessed(order.OrderId, order.PaymentId, 500_000m, DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new TicketsIssued(order.OrderId, [$"{order.OrderId}.pdf"], DateTimeOffset.UtcNow));

        Assert.True(await harness.Published.Any<OrderCompleted>());

        // The analytics write is now a message with its own retry and DLQ, so a Kafka outage costs
        // a delayed metric rather than blocking a paid, ticketed order from reaching Confirmed.
        Assert.Equal(1, harness.Published.Select<RelaySaleRecorded>().Count());
        Assert.Equal(1, harness.Published.Select<NotifyOrderCompleted>().Count());
    }

    private static async Task<(ITestHarness Harness, SagaOrderFixture Order)> StartAwaitingPaymentAsync()
    {
        var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var order = new SagaOrderFixture();

        await harness.Bus.Publish(order.Submitted());
        Assert.True(await harness.Published.Any<ConfirmSeats>());
        await harness.Bus.Publish(new SeatsConfirmed(order.OrderId, [order.SeatId], DateTimeOffset.UtcNow));
        Assert.True(await harness.Published.Any<ProcessPayment>());

        return (harness, order);
    }

    /// <summary>
    /// Waits on the strike count rather than on <c>InactivityTask</c>. That task is one-shot: once
    /// it has completed, every later await returns immediately, so a test that used it between
    /// firings read the saga before the deadline had been consumed and saw the strike count frozen.
    /// </summary>
    private static async Task FireDeadlineAsync(ITestHarness harness, SagaOrderFixture order)
    {
        var state = await LoadAsync(harness, order.OrderId);
        var before = state.DeadlineStrikes;

        await harness.Bus.Publish(
            new PurchaseDeadlineExpired(order.OrderId, state.CurrentState, state.DeadlineTokenId!.Value));

        Assert.True(
            await AsyncTest.WaitUntilAsync(
                () => LoadAsync(harness, order.OrderId).GetAwaiter().GetResult().DeadlineStrikes > before,
                SettleTimeout),
            $"The deadline for {state.CurrentState} was never processed.");
    }

    private static async Task<PurchaseState> LoadAsync(ITestHarness harness, Guid orderId)
    {
        var saga = harness.GetSagaStateMachineHarness<PurchaseSaga, PurchaseState>();
        var instance = saga.Sagas.Contains(orderId);
        Assert.NotNull(instance);
        return await Task.FromResult(instance);
    }

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(configuration =>
            {
                // The production definition, not a bare registration: it carries the partitioning,
                // the in-memory outbox and the missing-instance redelivery, so a test without it
                // would be measuring a saga endpoint nothing actually runs.
                configuration.AddDelayedMessageScheduler();
                configuration.AddSagaStateMachine<PurchaseSaga, PurchaseState, PurchaseSagaDefinition>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(validateScopes: true);
}

internal sealed class SagaOrderFixture
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
