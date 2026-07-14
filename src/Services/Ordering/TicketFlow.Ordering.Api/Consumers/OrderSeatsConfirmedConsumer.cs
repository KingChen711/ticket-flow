using MassTransit;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>
/// Independent of PurchaseSaga — both this consumer and the saga listen to the same
/// SeatsConfirmed integration event (RabbitMQ delivers a separate copy to each bound queue, see
/// PurchaseSaga's Publish-vs-Send doc comment). The saga uses it to drive its own transient state
/// machine; this consumer's only job is to append the matching Order-stream milestone, keeping
/// "orchestration" and "audit trail" as two genuinely separate concerns (architecture decision 3,
/// phase-5-plan.md). No Load first — Append only needs the stream to already exist (guaranteed:
/// PlaceOrder always starts it), and there's no guard/response to compute here unlike the manual
/// Confirm/Cancel command handlers.
/// </summary>
public sealed class OrderSeatsConfirmedConsumer(IOrderStore store, TimeProvider timeProvider) : IConsumer<SeatsConfirmed>
{
    public async Task Consume(ConsumeContext<SeatsConfirmed> context)
    {
        store.Append(context.Message.OrderId, new OrderSeatsConfirmed(context.Message.OrderId, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
