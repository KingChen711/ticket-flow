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
/// phase-5-plan.md).
/// </summary>
/// <remarks>
/// Loads first and skips if the stream doesn't exist — NOT an optimization, a correctness fix.
/// Originally this went straight to Append (no Load), on the assumption Marten requires the
/// target stream to already exist. Real bug caught during Commit 6 verification: publishing
/// SeatsConfirmed with a made-up OrderId (a throwaway scratch test value, unrelated to any real
/// order) silently created a brand-new garbage stream in Postgres instead of failing — Marten's
/// Append() does NOT require the stream to pre-exist, it happily starts one. A malformed or
/// mis-routed message would otherwise corrupt the event store with phantom orders instead of
/// being caught. Skipping (not throwing) on a missing order avoids a retry storm on a
/// permanently-invalid id — logged as a warning since it should never happen in the normal
/// saga-driven flow.
/// </remarks>
public sealed class OrderSeatsConfirmedConsumer(IOrderStore store, TimeProvider timeProvider, ILogger<OrderSeatsConfirmedConsumer> logger)
    : IConsumer<SeatsConfirmed>
{
    public async Task Consume(ConsumeContext<SeatsConfirmed> context)
    {
        var order = await store.LoadAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("SeatsConfirmed received for unknown OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        store.Append(context.Message.OrderId, new OrderSeatsConfirmed(context.Message.OrderId, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
