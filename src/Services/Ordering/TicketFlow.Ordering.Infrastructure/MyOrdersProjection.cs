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
        null,
        null);

    public MyOrderView Apply(OrderSeatsConfirmed e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.SeatsConfirmed);

    public MyOrderView Apply(OrderPaymentAuthorized e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.PaymentAuthorized);

    public MyOrderView Apply(OrderTicketsIssued e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.TicketsIssued) with { TicketPdfUrls = e.TicketPdfUrls };

    public MyOrderView Apply(OrderConfirmed e, MyOrderView current) =>
        current with { Status = nameof(OrderStatus.Confirmed), ConfirmedAt = e.ConfirmedAt };

    public MyOrderView Apply(OrderPaymentRefunded e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.Refunded);

    public MyOrderView Apply(OrderSeatsReleased e, MyOrderView current) =>
        WithIntermediateStatus(current, OrderStatus.SeatsReleased);

    public MyOrderView Apply(OrderCancelled e, MyOrderView current) =>
        current with { Status = nameof(OrderStatus.Cancelled), CancelledAt = e.CancelledAt };

    /// <summary>
    /// Monotonic guard (Commit 10), now defense-in-depth only (Commit 8).
    ///
    /// It was written for a real race: OrderSeatsReleasedConsumer and OrderCancelledConsumer sat
    /// on separate queues reacting to the same underlying delivery, RabbitMQ orders nothing
    /// between two separately-queued consumers, so OrderCancelled could be folded BEFORE
    /// OrderSeatsReleased and the later Apply would regress Status from terminal Cancelled back to
    /// intermediate SeatsReleased. That guard was masking a genuinely misordered write stream —
    /// the display value was corrected while the audit trail stayed wrong.
    ///
    /// Commit 8 removed the cause: one consumer and one version-checked writer now own the stream,
    /// and OrderMilestoneWriter's transition table refuses a milestone that would walk a terminal
    /// order backwards, so the events themselves can no longer arrive out of order. This stays as
    /// a second line of defence for streams written before that change and for any future writer
    /// that bypasses the funnel — but it is no longer the thing keeping the read model honest.
    /// </summary>
    private static MyOrderView WithIntermediateStatus(MyOrderView current, OrderStatus status) =>
        current.Status is nameof(OrderStatus.Confirmed) or nameof(OrderStatus.Cancelled)
            ? current
            : current with { Status = status.ToString() };
}
