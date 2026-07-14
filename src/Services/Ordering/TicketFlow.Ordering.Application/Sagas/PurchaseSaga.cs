using MassTransit;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;

namespace TicketFlow.Ordering.Application.Sagas;

/// <summary>
/// Orchestration saga (Ch9): a single coordinator drives reservation -> payment -> ticketing and
/// reacts to each participant's result event. Happy path only as of Commit 4 — compensation
/// branches (PaymentFailed/TicketingFailed/SeatsConfirmationFailed) land in Commit 9.
/// </summary>
/// <remarks>
/// Every step below uses <c>Publish</c>, including for the three commands (ConfirmSeats,
/// ProcessPayment, IssueTickets) that are conceptually targeted at exactly one participant.
/// MassTransit's RabbitMQ topology binds each consumer's queue to an exchange named after its
/// message type (via <c>ConfigureEndpoints</c>) regardless of whether that type is "logically" a
/// command or an event — <c>Send</c> only matters when you need to target a specific queue
/// address by URI, which would mean hardcoding each participant's <c>serviceName</c>-prefixed
/// kebab-case queue name here (fragile: defined in a different assembly, see
/// TicketFlow.Messaging's endpoint-formatter comment). Since each command type here has exactly
/// one bound consumer, <c>Publish</c> reaches it identically and keeps the saga decoupled from
/// participant queue-naming.
/// </remarks>
public sealed class PurchaseSaga : MassTransitStateMachine<PurchaseState>
{
    public State AwaitingSeats { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State AwaitingTickets { get; private set; } = null!;

    // Suffixed "Event" to avoid the exact class-of-bug Commit 2 hit (aggregate named `Payment`
    // colliding with the `TicketFlow.Payment` namespace root) — here it'd be a property named
    // identically to its message-type property, e.g. `Event<OrderSubmitted> OrderSubmitted`.
    public Event<OrderSubmitted> OrderSubmittedEvent { get; private set; } = null!;
    public Event<SeatsConfirmed> SeatsConfirmedEvent { get; private set; } = null!;
    public Event<PaymentProcessed> PaymentProcessedEvent { get; private set; } = null!;
    public Event<TicketsIssued> TicketsIssuedEvent { get; private set; } = null!;

    public PurchaseSaga()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderSubmittedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => SeatsConfirmedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => PaymentProcessedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => TicketsIssuedEvent, x => x.CorrelateById(context => context.Message.OrderId));

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
                .Publish(context => new ProcessPayment(
                    context.Saga.CorrelationId,
                    context.Saga.UserId,
                    context.Saga.Amount,
                    context.Saga.PaymentMethod))
                .TransitionTo(AwaitingPayment));

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
                .TransitionTo(AwaitingTickets));

        During(AwaitingTickets,
            When(TicketsIssuedEvent)
                .Then(context => context.Saga.TicketPdfUrls = [.. context.Message.TicketPdfUrls])
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
                    context.Saga.CustomerName,
                    context.Saga.CustomerEmail,
                    context.Saga.TicketPdfUrls))
                .Finalize());

        SetCompletedWhenFinalized();
    }
}
