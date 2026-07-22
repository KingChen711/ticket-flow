using Microsoft.Extensions.Logging;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Orders;

public enum OrderMilestone
{
    SeatsConfirmed,
    PaymentAuthorized,
    TicketsIssued,
    Confirmed,
    PaymentRefunded,
    SeatsReleased,
    Cancelled,
}

public sealed record OrderMilestoneRequest(
    Guid OrderId,
    OrderMilestone Milestone,
    Guid? PaymentId = null,
    IReadOnlyList<string>? TicketPdfUrls = null,
    string? Reason = null);

public enum OrderMilestoneOutcome
{
    /// <summary>The event was appended.</summary>
    Appended,

    /// <summary>Already recorded, or no longer meaningful for a terminal order. Nothing was written.</summary>
    Ignored,

    /// <summary>No stream — a mis-routed or malformed message.</summary>
    UnknownOrder,
}

public sealed record OrderMilestoneResult(OrderMilestoneOutcome Outcome, int SeatsSold = 0);

/// <summary>
/// The single writer of the order lifecycle stream (Commit 8). Six independent consumers used to
/// append to it from six independent queues, each doing a blind <c>Append</c> after a plain load —
/// two of them folding the same version could both append, which is how one order ended up with
/// two terminal transitions. All milestone traffic now funnels through one consumer and one
/// decision table here, and each append is checked against the version it was decided on.
/// </summary>
public sealed class OrderMilestoneWriter(IOrderStore store, TimeProvider timeProvider, ILogger<OrderMilestoneWriter> logger)
{
    /// <summary>
    /// Bounded because every retry is a fresh read of a stream someone else just moved; if that
    /// keeps happening the contention is the problem, not the retry count.
    /// </summary>
    private const int MaxConflictRetries = 5;

    public async Task<OrderMilestoneResult> RecordAsync(
        OrderMilestoneRequest request,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var writer = await store.FetchForWritingAsync(request.OrderId, cancellationToken);
            if (writer is null)
            {
                // Skipping rather than throwing avoids a retry storm on a permanently invalid id.
                // Marten's Append happily starts a stream that does not exist, so without this a
                // mis-routed message would create a phantom order instead of being caught.
                logger.LogWarning(
                    "[ordering] {Milestone} received for unknown OrderId {OrderId} — skipping.",
                    request.Milestone,
                    request.OrderId);
                return new OrderMilestoneResult(OrderMilestoneOutcome.UnknownOrder);
            }

            var decision = Decide(writer.Order, request, timeProvider.GetUtcNow());
            if (decision.Count == 0)
                return new OrderMilestoneResult(OrderMilestoneOutcome.Ignored);

            foreach (var @event in decision)
                writer.Append(@event);

            try
            {
                await store.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (store.IsConcurrencyConflict(exception) && attempt < MaxConflictRetries)
            {
                // Re-decide, never re-append: the winner may have already appended what this
                // message wanted, in which case the next pass correctly decides to do nothing.
                OrderMilestoneMetrics.RecordConflict(request.Milestone.ToString());
                logger.LogInformation(
                    "[ordering] {Milestone} for {OrderId} lost a version race; reloading and re-deciding.",
                    request.Milestone,
                    request.OrderId);
                continue;
            }

            OrderMilestoneMetrics.RecordAppended(request.Milestone.ToString());
            var seatsSold = decision.OfType<OrderConfirmed>().Any() ? writer.Order.SeatIds.Count : 0;
            return new OrderMilestoneResult(OrderMilestoneOutcome.Appended, seatsSold);
        }
    }

    /// <summary>
    /// The transition table. Everything it refuses is refused for one of two reasons: the fact is
    /// already in the stream (redelivery), or the order has already reached a terminal state and
    /// a late milestone must not walk it backwards.
    /// </summary>
    private static IReadOnlyList<object> Decide(Order order, OrderMilestoneRequest request, DateTimeOffset now)
    {
        var id = request.OrderId;
        var terminal = order.Status is OrderStatus.Confirmed or OrderStatus.Cancelled;

        return request.Milestone switch
        {
            OrderMilestone.SeatsConfirmed when !terminal && order.Status == OrderStatus.Pending =>
                [new OrderSeatsConfirmed(id, now)],

            OrderMilestone.PaymentAuthorized when !terminal && order.Status == OrderStatus.SeatsConfirmed =>
                [new OrderPaymentAuthorized(id, request.PaymentId ?? Guid.Empty, now)],

            OrderMilestone.TicketsIssued when !terminal && order.Status == OrderStatus.PaymentAuthorized =>
                [new OrderTicketsIssued(id, request.TicketPdfUrls ?? [], now)],

            // The saga only emits OrderCompleted after TicketsIssued, but the two facts can still
            // arrive together; appending both in one commit keeps the history complete without
            // assuming cross-queue delivery order.
            OrderMilestone.Confirmed when !terminal && order.Status == OrderStatus.PaymentAuthorized =>
                [new OrderTicketsIssued(id, request.TicketPdfUrls ?? [], now), new OrderConfirmed(id, now)],

            OrderMilestone.Confirmed when !terminal && order.Status == OrderStatus.TicketsIssued =>
                [new OrderConfirmed(id, now)],

            OrderMilestone.PaymentRefunded when !terminal && order.Status != OrderStatus.Refunded =>
                [new OrderPaymentRefunded(id, request.PaymentId ?? Guid.Empty, now)],

            OrderMilestone.SeatsReleased when !terminal && order.Status != OrderStatus.SeatsReleased =>
                [new OrderSeatsReleased(id, now)],

            OrderMilestone.Cancelled when !terminal =>
                [new OrderCancelled(id, request.Reason ?? "Compensation completed.", now)],

            _ => [],
        };
    }
}
