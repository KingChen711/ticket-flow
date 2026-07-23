using MassTransit;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;

namespace TicketFlow.Ordering.Application.Sagas;

/// <summary>
/// Orchestration saga (Ch9): a single coordinator drives reservation -> payment -> ticketing and
/// reacts to each participant's result event. Happy path (Commit 4) plus compensation branches
/// (Commit 9) — see the mermaid state diagram in phase-5-plan.md for the full picture.
/// </summary>
/// <remarks>
/// <para>
/// Commit 8 changed the shape of this machine. Every milestone used to be bound with
/// <c>During(SomeState, When(...))</c>, so a milestone that arrived in any other state matched no
/// handler and was silently dropped — and nothing ever redelivered it. All six delivery orders of
/// the three participant milestones were proven to hang the purchase, including the natural one,
/// because the test publishes each fact twice with no pause and the second copy could overtake
/// the first transition. Milestones are now bound with <see cref="MassTransitStateMachine{T}.DuringAny"/>:
/// each one records a fact on the instance, then <c>Advance</c> decides what the order is owed
/// next from the facts it holds. State is now a reflection of progress, not a gate on it.
/// </para>
/// <para>
/// The instance is no longer removed on completion (<c>SetCompletedWhenFinalized</c> is gone).
/// A removed instance cannot recognise a duplicate, so a late redelivery would look like a brand
/// new correlation; keeping a terminal instance is what makes "ignore this, we already finished"
/// expressible. Retention/archival of terminal instances belongs to Commit 9 along with deadlines.
/// </para>
/// <para>
/// Every step below uses <c>Publish</c>, including for the commands (ConfirmSeats,
/// ProcessPayment, IssueTickets, ReleaseSeats, RefundPayment) that are conceptually targeted at
/// exactly one participant. MassTransit's RabbitMQ topology binds each consumer's queue to an
/// exchange named after its message type (via <c>ConfigureEndpoints</c>) regardless of whether
/// that type is "logically" a command or an event — <c>Send</c> only matters when you need to
/// target a specific queue address by URI, which would mean hardcoding each participant's
/// <c>serviceName</c>-prefixed kebab-case queue name here (fragile: defined in a different
/// assembly, see TicketFlow.Messaging's endpoint-formatter comment). Since each command type here
/// has exactly one bound consumer, <c>Publish</c> reaches it identically and keeps the saga
/// decoupled from participant queue-naming.
/// </para>
/// </remarks>
public sealed class PurchaseSaga : MassTransitStateMachine<PurchaseState>
{
    public State AwaitingSeats { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State AwaitingTickets { get; private set; } = null!;
    public State CompensatingRefund { get; private set; } = null!;
    public State CompensatingSeats { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Cancelled { get; private set; } = null!;

    /// <summary>Automation gave up; an operator owns the order from here.</summary>
    public State ManualReview { get; private set; } = null!;

    public Event<PurchaseDeadlineExpired> DeadlineExpiredEvent { get; private set; } = null!;
    public Event<ResumePurchase> ResumeRequestedEvent { get; private set; } = null!;

    // Suffixed "Event" to avoid the exact class-of-bug Commit 2 hit (aggregate named `Payment`
    // colliding with the `TicketFlow.Payment` namespace root) — here it'd be a property named
    // identically to its message-type property, e.g. `Event<OrderSubmitted> OrderSubmitted`.
    public Event<OrderSubmitted> OrderSubmittedEvent { get; private set; } = null!;
    public Event<SeatsConfirmed> SeatsConfirmedEvent { get; private set; } = null!;
    public Event<SeatsConfirmationFailed> SeatsConfirmationFailedEvent { get; private set; } = null!;
    public Event<PaymentProcessed> PaymentProcessedEvent { get; private set; } = null!;
    public Event<PaymentFailed> PaymentFailedEvent { get; private set; } = null!;
    public Event<TicketsIssued> TicketsIssuedEvent { get; private set; } = null!;
    public Event<TicketingFailed> TicketingFailedEvent { get; private set; } = null!;
    public Event<PaymentRefunded> PaymentRefundedEvent { get; private set; } = null!;
    public Event<SeatsReleased> SeatsReleasedEvent { get; private set; } = null!;

    /// <summary>
    /// How long a participant may stay silent before the saga acts. Long enough that a slow
    /// payment provider or a brief outage is not mistaken for silence; short enough that a stuck
    /// order surfaces the same day.
    /// </summary>
    public static readonly TimeSpan ParticipantSilenceTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many times the saga re-sends the command it is waiting on before handing the order to a
    /// human. Every command it re-sends is idempotent (Commits 5-7), so a retry cannot double-charge
    /// or double-issue — but a participant that ignored three attempts is not going to answer the
    /// fourth, and pretending otherwise is how an order stays stuck forever.
    /// </summary>
    public const int MaxDeadlineStrikes = 3;

    public PurchaseSaga()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderSubmittedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        // Commit 9: every participant answer gets scheduled redelivery when no instance exists yet.
        // Only Initially creates one, so an answer that overtakes OrderSubmitted — the two are
        // published independently and RabbitMQ orders nothing between exchanges — used to match no
        // correlation and be discarded, leaving the purchase waiting forever on something that had
        // already happened. After the ladder is exhausted the message faults into the DLQ, so a
        // genuinely unknown OrderId still surfaces instead of looping.
        Event(() => SeatsConfirmedEvent, x => CorrelateAndRedeliver(x, m => m.Message.OrderId));
        Event(() => SeatsConfirmationFailedEvent, x => CorrelateAndRedeliver(x, m => m.Message.OrderId));
        Event(() => PaymentProcessedEvent, x => CorrelateAndRedeliver(x, m => m.Message.OrderId));
        Event(() => PaymentFailedEvent, x => CorrelateAndRedeliver(x, m => m.Message.OrderId));
        Event(() => TicketsIssuedEvent, x => CorrelateAndRedeliver(x, m => m.Message.OrderId));
        Event(() => TicketingFailedEvent, x => CorrelateAndRedeliver(x, m => m.Message.OrderId));
        Event(() => PaymentRefundedEvent, x => CorrelateAndRedeliver(x, m => m.Message.OrderId));
        Event(() => SeatsReleasedEvent, x => CorrelateAndRedeliver(x, m => m.Message.OrderId));

        Event(() => DeadlineExpiredEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => ResumeRequestedEvent, x => x.CorrelateById(context => context.Message.OrderId));

        Initially(
            When(OrderSubmittedEvent)
                .Then(context =>
                {
                    var message = context.Message;
                    context.Saga.UserId = message.UserId;
                    context.Saga.CatalogEventId = message.EventId;
                    context.Saga.HoldId = message.HoldId;
                    context.Saga.SeatIds = [.. message.SeatIds];
                    context.Saga.Amount = message.Amount;
                    context.Saga.Currency = message.Currency;
                    context.Saga.PriceSnapshotVersion = message.PriceSnapshotVersion;
                    context.Saga.AllocationGeneration = message.AllocationGeneration;
                    context.Saga.IdempotencyKey = message.IdempotencyKey ?? $"legacy:{message.OrderId}";
                    context.Saga.PaymentMethod = message.PaymentMethod;
                    context.Saga.CustomerName = message.CustomerName;
                    context.Saga.CustomerEmail = message.CustomerEmail;
                    context.Saga.SubmittedAt = message.SubmittedAt;
                })
                .Publish(context => new ConfirmSeats(
                    context.Saga.CorrelationId,
                    context.Saga.CatalogEventId,
                    context.Saga.HoldId,
                    context.Saga.SeatIds,
                    $"{context.Saga.IdempotencyKey}:confirm-seats",
                    context.Saga.PriceSnapshotVersion,
                    context.Saga.Amount,
                    context.Saga.Currency,
                    context.Saga.UserId,
                    context.Saga.AllocationGeneration))
                .TransitionTo(AwaitingSeats));

        // A duplicate OrderSubmitted is the outbox relay's at-least-once delivery (Commit 7)
        // reaching an instance that already exists. Re-running Initially would republish
        // ConfirmSeats, so it is explicitly a no-op instead.
        //
        // Listed state by state rather than with DuringAny: DuringAny includes Initial, and
        // binding this event there as well as in Initially registers two handlers for the same
        // event in the same state — the no-op can then shadow the real start.
        foreach (var state in new[]
                 {
                     AwaitingSeats, AwaitingPayment, AwaitingTickets,
                     CompensatingRefund, CompensatingSeats, Completed, Cancelled,
                 })
        {
            During(state, When(OrderSubmittedEvent).Then(_ => { }));
        }

        DuringAny(
            When(SeatsConfirmedEvent)
                .Then(context =>
                {
                    if (context.Saga.IsTerminal)
                        return;

                    // First writer wins; a redelivery finds the fact already recorded.
                    context.Saga.SeatsConfirmedAt ??= DateTimeOffset.UtcNow;
                    context.Saga.AllocationGeneration = context.Message.Generation;
                    context.Saga.AllocationReference = context.Message.AllocationReference;
                })
                .ThenAsync(AdvanceAsync),
            When(PaymentProcessedEvent)
                .Then(context =>
                {
                    if (context.Saga.IsTerminal)
                        return;

                    context.Saga.PaymentAuthorizedAt ??= DateTimeOffset.UtcNow;
                    context.Saga.PaymentId ??= context.Message.PaymentId;
                })
                .ThenAsync(AdvanceAsync),
            When(TicketsIssuedEvent)
                .Then(context =>
                {
                    if (context.Saga.IsTerminal)
                        return;

                    context.Saga.TicketsIssuedAt ??= DateTimeOffset.UtcNow;
                    if (context.Saga.TicketPdfUrls.Count == 0)
                        context.Saga.TicketPdfUrls = [.. context.Message.TicketPdfUrls];
                })
                .ThenAsync(AdvanceAsync));

        DuringAny(
            // Hold expired/missing before checkout completed — nothing charged, nothing to
            // release. Straight to Cancelled, no compensation commands needed (DoD scenario 3).
            When(SeatsConfirmationFailedEvent)
                .ThenAsync(context => FailAsync(context, context.Message.Reason, compensate: false)),
            // Nothing charged — release the hold directly (DoD scenario 1).
            When(PaymentFailedEvent)
                .ThenAsync(context => FailAsync(context, context.Message.Reason, compensate: true)),
            // Already charged — refund first, THEN release the hold (DoD scenario 2).
            When(TicketingFailedEvent)
                .ThenAsync(context => FailAsync(context, context.Message.Reason, compensate: true)),
            When(PaymentRefundedEvent)
                .Then(context => context.Saga.PaymentRefundedAt ??= DateTimeOffset.UtcNow)
                .ThenAsync(CompensateAsync),
            When(SeatsReleasedEvent)
                .Then(context => context.Saga.SeatsReleasedAt ??= DateTimeOffset.UtcNow)
                .ThenAsync(CompensateAsync));

        DuringAny(
            When(DeadlineExpiredEvent).ThenAsync(OnDeadlineAsync),
            When(ResumeRequestedEvent).ThenAsync(OnResumeAsync));
    }

    /// <summary>
    /// The whole decision table, evaluated from recorded facts only. Reading it top to bottom is
    /// the order's actual dependency chain; nothing here depends on which message just arrived or
    /// on the current state, which is exactly why every delivery order converges.
    /// </summary>
    private static async Task AdvanceAsync(BehaviorContext<PurchaseState> context)
    {
        var saga = context.Saga;
        if (saga.IsTerminal || saga.CancellationReason is not null)
            return;

        if (saga.SeatsConfirmedAt is null)
            return;

        if (saga.PaymentAuthorizedAt is null)
        {
            if (saga.PaymentRequestedAt is null)
            {
                saga.PaymentRequestedAt = DateTimeOffset.UtcNow;
                await context.Publish(new ProcessPayment(
                    saga.CorrelationId,
                    saga.UserId,
                    saga.Amount,
                    saga.PaymentMethod,
                    saga.Currency,
                    $"{saga.IdempotencyKey}:charge",
                    saga.PriceSnapshotVersion));
            }

            await TransitionAsync(context, "AwaitingPayment");
            return;
        }

        if (saga.TicketsIssuedAt is null)
        {
            if (saga.TicketsRequestedAt is null)
            {
                saga.TicketsRequestedAt = DateTimeOffset.UtcNow;
                await context.Publish(new IssueTickets(
                    saga.CorrelationId,
                    saga.UserId,
                    saga.CatalogEventId,
                    saga.SeatIds,
                    saga.CustomerName,
                    saga.CustomerEmail,
                    $"{saga.IdempotencyKey}:issue",
                    saga.PriceSnapshotVersion));
            }

            await TransitionAsync(context, "AwaitingTickets");
            return;
        }

        saga.CompletedAt = DateTimeOffset.UtcNow;

        // Commit 9: the Kafka producer used to be called right here, inline. That put an analytics
        // stream on the critical path of a paid, ticketed order — a Kafka outage threw out of this
        // activity and the purchase never reached Confirmed. It is now a RabbitMQ message handled
        // by Ordering's own relay consumer, so the analytics write has its own retry and DLQ.
        //
        // (The old inline call also had to resolve ISaleRecordedPublisher lazily from the scope,
        // because constructor-injecting ITopicProducer into the saga deadlocked bus startup: the
        // bus must construct the saga to register its endpoint, which needed the Kafka Rider that
        // was still being configured on that same bus. That whole hazard disappears with the
        // producer no longer reachable from the saga at all.)
        await context.Publish(new RelaySaleRecorded(
            saga.CorrelationId,
            saga.CatalogEventId,
            saga.SeatIds.Count,
            saga.Amount,
            saga.CompletedAt.Value,
            saga.Currency,
            saga.PriceSnapshotVersion));

        await context.Publish(new OrderCompleted(
            saga.CorrelationId,
            saga.UserId,
            saga.CatalogEventId,
            saga.SeatIds,
            saga.TicketPdfUrls,
            // Stable, persisted before publication, so a redelivery cannot invent a second
            // completion time for the same order.
            saga.CompletedAt.Value));

        await context.Publish(new NotifyOrderCompleted(
            saga.CorrelationId,
            saga.UserId,
            saga.CatalogEventId,
            saga.CustomerName,
            saga.CustomerEmail,
            saga.TicketPdfUrls));

        await TransitionAsync(context, "Completed");
    }

    private static async Task FailAsync(BehaviorContext<PurchaseState> context, string reason, bool compensate)
    {
        var saga = context.Saga;
        if (saga.IsTerminal)
            return;

        // A failure after the order already completed is a late duplicate, not a reversal.
        if (saga.CompletedAt is not null)
            return;

        saga.CancellationReason ??= reason;

        if (!compensate)
        {
            await FinishCancellationAsync(context);
            return;
        }

        await CompensateAsync(context);
    }

    /// <summary>
    /// Compensation as a decision table too, for the same reason: a PaymentRefunded that overtakes
    /// the TicketingFailed that asked for it must not be lost.
    /// </summary>
    private static async Task CompensateAsync(BehaviorContext<PurchaseState> context)
    {
        var saga = context.Saga;
        if (saga.IsTerminal || saga.CancellationReason is null)
            return;

        // Only a charge that actually happened needs reversing.
        if (saga.PaymentAuthorizedAt is not null && saga.PaymentRefundedAt is null)
        {
            if (saga.RefundRequestedAt is null)
            {
                saga.RefundRequestedAt = DateTimeOffset.UtcNow;
                await context.Publish(new RefundPayment(
                    saga.CorrelationId,
                    saga.PaymentId!.Value,
                    saga.Amount,
                    saga.Currency,
                    $"{saga.IdempotencyKey}:refund"));
            }

            await TransitionAsync(context, "CompensatingRefund");
            return;
        }

        if (saga.SeatsReleasedAt is null)
        {
            if (saga.ReleaseRequestedAt is null)
            {
                saga.ReleaseRequestedAt = DateTimeOffset.UtcNow;
                await context.Publish(new ReleaseSeats(
                    saga.CorrelationId,
                    saga.CatalogEventId,
                    saga.HoldId,
                    saga.SeatIds,
                    $"{saga.IdempotencyKey}:release",
                    saga.AllocationGeneration,
                    saga.UserId));
            }

            await TransitionAsync(context, "CompensatingSeats");
            return;
        }

        await FinishCancellationAsync(context);
    }

    private static async Task FinishCancellationAsync(BehaviorContext<PurchaseState> context)
    {
        var saga = context.Saga;
        saga.CancelledAt = DateTimeOffset.UtcNow;

        await context.Publish(new OrderCancelledIntegrationEvent(
            saga.CorrelationId,
            saga.UserId,
            saga.CancellationReason ?? "Compensation completed.",
            saga.CancelledAt.Value));

        await TransitionAsync(context, "Cancelled");
    }

    /// <summary>
    /// Correlates by OrderId and, when the instance does not exist yet, schedules the message back
    /// instead of discarding it. Short first, because the usual case is a race of milliseconds
    /// between two publishes; the longer tail covers a slow checkout commit.
    /// </summary>
    private static void CorrelateAndRedeliver<T>(
        IEventCorrelationConfigurator<PurchaseState, T> configurator,
        Func<ConsumeContext<T>, Guid> correlate)
        where T : class
    {
        configurator.CorrelateById(correlate);
        configurator.OnMissingInstance(instance => instance.Redeliver(redelivery =>
        {
            redelivery.Intervals(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30));
            redelivery.OnRedeliveryLimitReached(context => context.Fault());
        }));
    }

    /// <summary>
    /// An operator pulled an order out of ManualReview. Resetting the strike count is the whole
    /// mechanism: the saga then re-sends the command it is owed and starts waiting again, exactly
    /// as if the participant had merely been slow.
    /// </summary>
    private static async Task OnResumeAsync(BehaviorContext<PurchaseState, ResumePurchase> context)
    {
        var saga = context.Saga;

        // A finished order has an outcome; "try again" is meaningless and must not undo it.
        if (saga.CompletedAt is not null || saga.CancelledAt is not null)
            return;

        saga.ManualReviewAt = null;
        saga.ManualReviewReason = null;
        saga.DeadlineStrikes = 0;

        // Clear every "already asked" marker for steps that have not produced their fact yet, so
        // Advance re-sends them. A step whose fact is already recorded is left alone.
        if (saga.PaymentAuthorizedAt is null)
            saga.PaymentRequestedAt = null;
        if (saga.TicketsIssuedAt is null)
            saga.TicketsRequestedAt = null;
        if (saga.PaymentRefundedAt is null)
            saga.RefundRequestedAt = null;
        if (saga.SeatsReleasedAt is null)
            saga.ReleaseRequestedAt = null;

        PurchaseSagaMetrics.RecordResume(context.Message.RequestedBy);

        if (saga.SeatsConfirmedAt is null)
        {
            await context.Publish(new ConfirmSeats(
                saga.CorrelationId,
                saga.CatalogEventId,
                saga.HoldId,
                saga.SeatIds,
                $"{saga.IdempotencyKey}:confirm-seats",
                saga.PriceSnapshotVersion,
                saga.Amount,
                saga.Currency,
                saga.UserId,
                saga.AllocationGeneration));
            await TransitionAsync(context, "AwaitingSeats");
            return;
        }

        if (saga.CancellationReason is not null)
            await CompensateAsync(context);
        else
            await AdvanceAsync(context);
    }

    /// <summary>
    /// The DSL's <c>TransitionTo</c> only exists on an activity binder, and the target here is
    /// decided at run time from the facts. Resolving the state by name off the machine is the
    /// supported way to move an instance from inside an activity.
    /// </summary>
    /// <remarks>
    /// Every path through this machine funnels here, which is why the deadline is armed and
    /// cancelled here rather than at each call site: a waiting state always gets a deadline, and a
    /// terminal state always drops it, with no branch able to forget one.
    /// </remarks>
    private static async Task TransitionAsync(BehaviorContext<PurchaseState> context, string stateName)
    {
        var machine = (PurchaseSaga)context.StateMachine;
        var saga = context.Saga;
        var isWaiting = stateName is "AwaitingSeats" or "AwaitingPayment" or "AwaitingTickets"
            or "CompensatingRefund" or "CompensatingSeats";

        if (saga.CurrentState != stateName)
            await context.TransitionToState(machine.GetState(stateName));

        if (!isWaiting)
        {
            // Terminal: any deadline still in flight now carries a stale token and is ignored on
            // arrival, so there is nothing to cancel.
            saga.DeadlineTokenId = null;
            return;
        }

        var token = Guid.NewGuid();
        saga.DeadlineTokenId = token;
        await context.ScheduleSend(
            ParticipantSilenceTimeout,
            new PurchaseDeadlineExpired(saga.CorrelationId, stateName, token));
    }

    /// <summary>
    /// A deadline fired. Either the participant is merely slow — re-send the command it owes us,
    /// which is safe because every participant command is idempotent — or it has ignored enough
    /// attempts that a human has to look.
    /// </summary>
    private static async Task OnDeadlineAsync(BehaviorContext<PurchaseState, PurchaseDeadlineExpired> context)
    {
        var saga = context.Saga;
        if (saga.IsTerminal)
            return;

        // Stale: the participant answered, or the wait was re-armed, while this deadline was
        // already in flight. Treating it as silence would restart a wait that has ended, or count
        // one silence twice.
        if (saga.DeadlineTokenId != context.Message.Token ||
            saga.CurrentState != context.Message.ExpectedState)
            return;

        saga.DeadlineStrikes++;
        PurchaseSagaMetrics.RecordDeadline(context.Message.ExpectedState);

        if (saga.DeadlineStrikes > MaxDeadlineStrikes)
        {
            saga.ManualReviewAt = DateTimeOffset.UtcNow;
            saga.ManualReviewReason =
                $"No response in {context.Message.ExpectedState} after {MaxDeadlineStrikes} retries.";
            PurchaseSagaMetrics.RecordManualReview(context.Message.ExpectedState);

            // Deliberately publishes nothing. A stuck order is not a cancelled order: cancelling on
            // silence could release seats for a purchase that was in fact paid, and the ambiguous
            // payment case already has its own reconciliation (Commit 6).
            await TransitionAsync(context, "ManualReview");
            return;
        }

        // Clear the "already asked" marker for the step actually being waited on, so Advance
        // re-sends exactly that one command and no other.
        switch (context.Message.ExpectedState)
        {
            case "AwaitingPayment":
                saga.PaymentRequestedAt = null;
                break;
            case "AwaitingTickets":
                saga.TicketsRequestedAt = null;
                break;
            case "CompensatingRefund":
                saga.RefundRequestedAt = null;
                break;
            case "CompensatingSeats":
                saga.ReleaseRequestedAt = null;
                break;
            case "AwaitingSeats":
                await context.Publish(new ConfirmSeats(
                    saga.CorrelationId,
                    saga.CatalogEventId,
                    saga.HoldId,
                    saga.SeatIds,
                    $"{saga.IdempotencyKey}:confirm-seats",
                    saga.PriceSnapshotVersion,
                    saga.Amount,
                    saga.Currency,
                    saga.UserId,
                    saga.AllocationGeneration));
                await TransitionAsync(context, "AwaitingSeats");
                return;
        }

        if (saga.CancellationReason is not null)
            await CompensateAsync(context);
        else
            await AdvanceAsync(context);
    }
}
