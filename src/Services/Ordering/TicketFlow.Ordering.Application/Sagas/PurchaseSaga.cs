using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Sagas;

/// <summary>
/// Orchestration saga (Ch9): a single coordinator drives reservation -> payment -> ticketing and
/// reacts to each participant's result event. Happy path (Commit 4) plus compensation branches
/// (Commit 9) — see the mermaid state diagram in phase-5-plan.md for the full picture.
/// </summary>
/// <remarks>
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
/// </remarks>
public sealed class PurchaseSaga : MassTransitStateMachine<PurchaseState>
{
    public State AwaitingSeats { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State AwaitingTickets { get; private set; } = null!;
    public State CompensatingRefund { get; private set; } = null!;
    public State CompensatingSeats { get; private set; } = null!;

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

    public PurchaseSaga()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderSubmittedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => SeatsConfirmedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => SeatsConfirmationFailedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => PaymentProcessedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => PaymentFailedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => TicketsIssuedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => TicketingFailedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => PaymentRefundedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => SeatsReleasedEvent, x => x.CorrelateById(context => context.Message.OrderId));

        Initially(
            When(OrderSubmittedEvent)
                .Then(context =>
                {
                    var message = context.Message;
                    context.Saga.UserId = message.UserId;
                    context.Saga.CatalogEventId = message.EventId;
                    context.Saga.HoldId = message.HoldId;
                    context.Saga.SeatIds = [.. message.SeatIds];
                    context.Saga.RequestedSeatIds = [.. message.SeatIds];
                    context.Saga.Amount = message.Amount;
                    context.Saga.PaymentMethod = message.PaymentMethod;
                    context.Saga.CustomerName = message.CustomerName;
                    context.Saga.CustomerEmail = message.CustomerEmail;
                    context.Saga.SubmittedAt = message.SubmittedAt;
                })
                .Publish(context => new ConfirmSeats(
                    context.Saga.CorrelationId,
                    context.Saga.CatalogEventId,
                    context.Saga.HoldId,
                    context.Saga.SeatIds))
                .TransitionTo(AwaitingSeats));

        During(AwaitingSeats,
            When(SeatsConfirmedEvent)
                // From here on the saga works with the seats reservation actually confirmed from
                // the hold, not the ids the client listed in the order request: IssueTickets and
                // the ReleaseSeats compensation (which un-sells every listed seat) must never act
                // on a seat that belongs to someone else's hold or sale.
                .Then(context => context.Saga.RecordConfirmedSeats(context.Message.SeatIds))
                .IfElse(context => context.Saga.ConfirmedAllRequestedSeats(),
                    confirmed => confirmed
                        .Publish(context => new ProcessPayment(
                            context.Saga.CorrelationId,
                            context.Saga.UserId,
                            context.Saga.Amount,
                            context.Saga.PaymentMethod))
                        .TransitionTo(AwaitingPayment),
                    // Part of the hold expired (or the request listed seats outside it). The amount
                    // was priced for every requested seat, so charging it would overcharge: give
                    // back what was confirmed and cancel, nothing has been charged yet.
                    partial => partial
                        .Then(context => context.Saga.CancellationReason =
                            "Only part of the requested seats could be confirmed from the hold.")
                        .Publish(context => new ReleaseSeats(
                            context.Saga.CorrelationId,
                            context.Saga.CatalogEventId,
                            context.Saga.HoldId,
                            context.Saga.SeatIds))
                        .TransitionTo(CompensatingSeats)),
            // Hold expired/missing before checkout completed — nothing charged, nothing to
            // release. Straight to Cancelled, no compensation commands needed (DoD scenario 3).
            When(SeatsConfirmationFailedEvent)
                .Then(context => context.Saga.CancellationReason = context.Message.Reason)
                .Publish(context => new OrderCancelledIntegrationEvent(
                    context.Saga.CorrelationId, context.Saga.UserId, context.Saga.CancellationReason!, DateTimeOffset.UtcNow))
                .Finalize());

        During(AwaitingPayment,
            When(PaymentProcessedEvent)
                .Then(context => context.Saga.PaymentId = context.Message.PaymentId)
                .Publish(context => new IssueTickets(
                    context.Saga.CorrelationId,
                    context.Saga.UserId,
                    context.Saga.CatalogEventId,
                    context.Saga.SeatIds,
                    context.Saga.CustomerName,
                    context.Saga.CustomerEmail))
                .TransitionTo(AwaitingTickets),
            // Nothing charged — release the hold directly (DoD scenario 1).
            When(PaymentFailedEvent)
                .Then(context => context.Saga.CancellationReason = context.Message.Reason)
                .Publish(context => new ReleaseSeats(
                    context.Saga.CorrelationId,
                    context.Saga.CatalogEventId,
                    context.Saga.HoldId,
                    context.Saga.SeatIds))
                .TransitionTo(CompensatingSeats));

        During(AwaitingTickets,
            When(TicketsIssuedEvent)
                .Then(context => context.Saga.TicketPdfUrls = [.. context.Message.TicketPdfUrls])
                // Same business moment as OrderCompleted/NotifyOrderCompleted below, different
                // transport (Kafka, not RabbitMQ) and different purpose (analytics stream, not a
                // command/notification) — Phase 6 mục 3's "2 transport, 2 mục đích" made concrete.
                // Ordered before the two Publish calls, not after: Finalize() must directly follow
                // a synchronous DSL activity — placing ThenAsync immediately before Finalize()
                // causes Finalize() to silently never run (saga instance stays un-removed),
                // confirmed by re-running this suite repeatedly with a temporary diagnostic that
                // showed ThenAsync completing cleanly yet the instance still existing afterward.
                //
                // ISaleRecordedPublisher is deliberately resolved HERE (consume-time, via the
                // scope's IServiceProvider), not constructor-injected into PurchaseSaga. It
                // ultimately depends on ITopicProducer<SaleRecorded>, which the Kafka Rider
                // attached to this SAME bus provides — constructor-injecting it deadlocks the
                // whole bus at startup (MassTransit has to construct the saga to register its
                // endpoint, which needs the Rider's producer, which needs the Rider's own
                // startup on the bus still being configured). Confirmed by reproducing the exact
                // hang in an isolated probe with a saga constructor depending on ITopicProducer,
                // then confirming it disappears once resolved lazily instead.
                .ThenAsync(async context =>
                {
                    var saleRecordedPublisher = context.GetPayload<IServiceProvider>().GetRequiredService<ISaleRecordedPublisher>();
                    await saleRecordedPublisher.PublishAsync(
                        new SaleRecorded(
                            context.Saga.CorrelationId,
                            context.Saga.CatalogEventId,
                            context.Saga.SeatIds.Count,
                            context.Saga.Amount,
                            DateTimeOffset.UtcNow),
                        context.CancellationToken);
                })
                .Publish(context => new OrderCompleted(
                    context.Saga.CorrelationId,
                    context.Saga.UserId,
                    context.Saga.CatalogEventId,
                    context.Saga.SeatIds,
                    context.Saga.TicketPdfUrls,
                    DateTimeOffset.UtcNow))
                .Publish(context => new NotifyOrderCompleted(
                    context.Saga.CorrelationId,
                    context.Saga.UserId,
                    context.Saga.CatalogEventId,
                    context.Saga.CustomerName,
                    context.Saga.CustomerEmail,
                    context.Saga.TicketPdfUrls))
                .Finalize(),
            // Already charged — refund first, THEN release the hold (DoD scenario 2).
            When(TicketingFailedEvent)
                .Then(context => context.Saga.CancellationReason = context.Message.Reason)
                .Publish(context => new RefundPayment(
                    context.Saga.CorrelationId,
                    context.Saga.PaymentId!.Value,
                    context.Saga.Amount))
                .TransitionTo(CompensatingRefund));

        During(CompensatingRefund,
            When(PaymentRefundedEvent)
                .Publish(context => new ReleaseSeats(
                    context.Saga.CorrelationId,
                    context.Saga.CatalogEventId,
                    context.Saga.HoldId,
                    context.Saga.SeatIds))
                .TransitionTo(CompensatingSeats));

        // Both compensation paths (PaymentFailed direct, TicketingFailed via CompensatingRefund)
        // converge here — the seats are released either way, then the order is finally cancelled.
        During(CompensatingSeats,
            When(SeatsReleasedEvent)
                .Publish(context => new OrderCancelledIntegrationEvent(
                    context.Saga.CorrelationId,
                    context.Saga.UserId,
                    context.Saga.CancellationReason ?? "Compensation completed.",
                    DateTimeOffset.UtcNow))
                .Finalize());

        // A re-sent OrderSubmitted (the outbox is at-least-once) for an order already in flight.
        DuringAny(Ignore(OrderSubmittedEvent));

        SetCompletedWhenFinalized();
    }
}
