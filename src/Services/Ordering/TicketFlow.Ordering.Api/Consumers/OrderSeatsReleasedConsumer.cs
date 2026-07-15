using MassTransit;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>
/// Compensation milestone (Commit 9). Same shape as OrderSeatsConfirmedConsumer (incl. the
/// Load-first guard — see its doc comment for the garbage-stream bug that guard fixes). Fires on
/// SeatsReleased regardless of which compensation path the saga took (PaymentFailed direct, or
/// TicketingFailed via refund) — both converge on releasing the hold.
/// </summary>
public sealed class OrderSeatsReleasedConsumer(IOrderStore store, TimeProvider timeProvider, ILogger<OrderSeatsReleasedConsumer> logger)
    : IConsumer<SeatsReleased>
{
    public async Task Consume(ConsumeContext<SeatsReleased> context)
    {
        var order = await store.LoadAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("SeatsReleased received for unknown OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        store.Append(context.Message.OrderId, new OrderSeatsReleased(context.Message.OrderId, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
