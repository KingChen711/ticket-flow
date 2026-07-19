using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Api.Metrics;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>
/// Final audit milestone. The saga emits <see cref="OrderCompleted"/> only after receiving
/// <c>TicketsIssued</c>, so this consumer appends both the ticket-issued audit event and the
/// existing <see cref="OrderConfirmed"/> event in one Marten save. They used to be two separate
/// consumers on independent RabbitMQ queues; under a flash-sale burst both could load the same
/// PaymentAuthorized stream and race to append version 4. Keeping the dependent writes atomic
/// preserves the full event history without assuming cross-queue delivery order.
///
/// Phase 10 Commit 3 — also where the `ticketflow.tickets.sold` counter increments (see
/// TicketSalesMetrics's doc comment for why here, not the saga or Analytics). Recorded only after
/// the unknown-OrderId guard, same as the domain event append below it — a message for an order
/// this projection never saw isn't a real sale from this read model's point of view either.
/// </summary>
public sealed class OrderCompletedConsumer(
    IOrderStore store,
    TimeProvider timeProvider,
    TicketSalesMetrics metrics,
    ILogger<OrderCompletedConsumer> logger)
    : IConsumer<OrderCompleted>
{
    public async Task Consume(ConsumeContext<OrderCompleted> context)
    {
        var order = await store.LoadAsync(context.Message.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogWarning("OrderCompleted received for unknown OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        if (order.Status == OrderStatus.Confirmed)
        {
            logger.LogInformation("OrderCompleted replay for already-confirmed OrderId {OrderId} — skipping.", context.Message.OrderId);
            return;
        }

        if (order.Status == OrderStatus.PaymentAuthorized)
        {
            store.Append(
                context.Message.OrderId,
                new OrderTicketsIssued(context.Message.OrderId, context.Message.TicketPdfUrls, timeProvider.GetUtcNow()));
        }
        else if (order.Status != OrderStatus.TicketsIssued)
        {
            // The payment milestone should always precede the saga's OrderCompleted publication.
            // Throwing engages the shared bounded MassTransit retry instead of acknowledging an
            // out-of-order message and permanently dropping the final confirmation.
            throw new InvalidOperationException(
                $"OrderCompleted arrived for {context.Message.OrderId} while the order is {order.Status}, not PaymentAuthorized/TicketsIssued.");
        }

        store.Append(context.Message.OrderId, new OrderConfirmed(context.Message.OrderId, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);

        metrics.RecordTicketsSold(context.Message.SeatIds.Count);
    }
}
