using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Ordering.Api.Metrics;
using TicketFlow.Ordering.Application.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>
/// The one consumer that writes the order lifecycle stream (Commit 8), replacing the six
/// independent per-message consumers that each owned their own queue.
/// </summary>
/// <remarks>
/// <para>
/// Six queues meant six consumers could be inside the same order's stream at once, each doing a
/// blind append after a plain load. Binding every milestone type to a single consumer on a single
/// receive endpoint collapses that to one in-flight message per delivery, and
/// <see cref="OrderMilestoneWriter"/>'s version-checked append covers what is left: redelivery,
/// competing instances of this service, and the operator endpoints.
/// </para>
/// <para>
/// The writer does not rely on any delivery ordering: its transition table is order-independent
/// and the version check turns a lost race into a re-decide rather than a double-append. That is
/// deliberate, because this endpoint is not partitioned — only the saga is (see
/// <c>PurchaseSagaDefinition</c>, which partitions by OrderId). Competing instances of this
/// service and redelivery both still land here concurrently.
/// </para>
/// </remarks>
public sealed class OrderMilestoneConsumer(OrderMilestoneWriter writer, TicketSalesMetrics metrics) :
    IConsumer<SeatsConfirmed>,
    IConsumer<PaymentProcessed>,
    IConsumer<OrderCompleted>,
    IConsumer<PaymentRefunded>,
    IConsumer<SeatsReleased>,
    IConsumer<OrderCancelledIntegrationEvent>
{
    public async Task Consume(ConsumeContext<SeatsConfirmed> context)
    {
        ContractVersionMetrics.RecordInbound(nameof(SeatsConfirmed), context.Message.AggregateVersion);
        await writer.RecordAsync(
            new OrderMilestoneRequest(context.Message.OrderId, OrderMilestone.SeatsConfirmed),
            context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<PaymentProcessed> context)
    {
        ContractVersionMetrics.RecordInbound(nameof(PaymentProcessed), context.Message.AggregateVersion);
        await writer.RecordAsync(
            new OrderMilestoneRequest(
                context.Message.OrderId, OrderMilestone.PaymentAuthorized, PaymentId: context.Message.PaymentId),
            context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<OrderCompleted> context)
    {
        ContractVersionMetrics.RecordInbound(nameof(OrderCompleted), context.Message.AggregateVersion);
        var result = await writer.RecordAsync(
            new OrderMilestoneRequest(
                context.Message.OrderId, OrderMilestone.Confirmed, TicketPdfUrls: context.Message.TicketPdfUrls),
            context.CancellationToken);

        // Only a commit that actually appended the terminal transition counts a sale. The counter
        // used to be incremented after every delivery, outside the decision entirely, so a
        // redelivered OrderCompleted inflated "vé bán/phút" for an order that sold once.
        if (result.Outcome == OrderMilestoneOutcome.Appended && result.SeatsSold > 0)
            metrics.RecordTicketsSold(result.SeatsSold);
    }

    public async Task Consume(ConsumeContext<PaymentRefunded> context)
    {
        ContractVersionMetrics.RecordInbound(nameof(PaymentRefunded), context.Message.AggregateVersion);
        await writer.RecordAsync(
            new OrderMilestoneRequest(
                context.Message.OrderId, OrderMilestone.PaymentRefunded, PaymentId: context.Message.PaymentId),
            context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<SeatsReleased> context) =>
        await writer.RecordAsync(
            new OrderMilestoneRequest(context.Message.OrderId, OrderMilestone.SeatsReleased),
            context.CancellationToken);

    public async Task Consume(ConsumeContext<OrderCancelledIntegrationEvent> context) =>
        await writer.RecordAsync(
            new OrderMilestoneRequest(
                context.Message.OrderId, OrderMilestone.Cancelled, Reason: context.Message.Reason),
            context.CancellationToken);
}
