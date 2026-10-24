using Marten.Events.Aggregation;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Infrastructure;

/// <summary>
/// Explicit, separate projection into MyOrderView — deliberately NOT Projections.Snapshot&lt;Order&gt;
/// (which would just persist the write-side aggregate itself as a document, collapsing read and
/// write into the same model). This is the actual CQRS split Commit 5/6 exist to demonstrate:
/// write side folds into Order (Commit 1-2), read side folds the SAME events into a differently
/// shaped, query-optimized document.
///
/// Must be `partial` — unlike a self-aggregating type (Order, Commit 1-2), a *projection
/// subclass* using convention Apply/Create methods needs the compile-time source generator to
/// inject generated dispatch code directly into this class, not just alongside it. Missing
/// `partial` throws InvalidProjectionException at startup with no runtime fallback (caught
/// during this commit's verification — the error message names this exact requirement, once
/// read carefully instead of assuming it was another instance of the Commit 2 analyzer issue).
/// </summary>
public sealed partial class MyOrdersProjection : SingleStreamProjection<MyOrderView, Guid>
{
    public static MyOrderView Create(OrderPlaced e) => new(
        e.OrderId,
        e.CustomerId,
        e.CatalogEventId,
        e.SeatIds,
        e.TotalAmount,
        e.Currency,
        nameof(OrderStatus.Pending),
        e.PlacedAt,
        null,
        null);

    public MyOrderView Apply(OrderSeatsConfirmed e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.SeatsConfirmed) with
        {
            SeatIds = e.SeatIds is { Count: > 0 } ? e.SeatIds : current.SeatIds
        };

    public MyOrderView Apply(OrderPaymentAuthorized e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.PaymentAuthorized);

    public MyOrderView Apply(OrderTicketsIssued e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.TicketsIssued);

    public MyOrderView Apply(OrderConfirmed e, MyOrderView current) =>
        current with { Status = nameof(OrderStatus.Confirmed), ConfirmedAt = e.ConfirmedAt };

    public MyOrderView Apply(OrderPaymentRefunded e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.Refunded);

    public MyOrderView Apply(OrderSeatsReleased e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.SeatsReleased);

    public MyOrderView Apply(OrderCancelled e, MyOrderView current) =>
        current with { Status = nameof(OrderStatus.Cancelled), CancelledAt = e.CancelledAt };

    /// <summary>
    /// Monotonic guard (Commit 10) — real race found while re-verifying the compensation DoD:
    /// OrderSeatsReleasedConsumer and OrderCancelledConsumer both react independently to the same
    /// underlying SeatsReleased delivery (one via the saga's own SeatsReleased handler that then
    /// publishes OrderCancelledIntegrationEvent, one via this projection's own direct consumer) —
    /// RabbitMQ gives no ordering guarantee between two separately-queued consumers of the same
    /// message, so OrderCancelled can occasionally get folded BEFORE OrderSeatsReleased, and
    /// without this guard the later Apply would silently regress Status from the terminal
    /// Cancelled back to the intermediate SeatsReleased. Once a stream reaches a terminal status
    /// (Confirmed/Cancelled), no intermediate milestone may downgrade it — the terminal event
    /// itself is still the one that recorded the real outcome; only the read model's display
    /// value was at risk. The underlying event STREAM can still show the two events in either
    /// relative order (a real, smaller audit-trail imperfection, not fixed here — see
    /// phase-5-plan.md Commit 10).
    /// </summary>
    private static MyOrderView WithIntermediateStatus(MyOrderView current, OrderStatus status) =>
        current.Status is nameof(OrderStatus.Confirmed) or nameof(OrderStatus.Cancelled)
            ? current
            : current with { Status = status.ToString() };
}
