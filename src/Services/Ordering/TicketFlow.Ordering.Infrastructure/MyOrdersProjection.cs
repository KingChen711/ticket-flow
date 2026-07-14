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
        current with { Status = nameof(OrderStatus.SeatsConfirmed) };

    public MyOrderView Apply(OrderPaymentAuthorized e, MyOrderView current) =>
        current with { Status = nameof(OrderStatus.PaymentAuthorized) };

    public MyOrderView Apply(OrderTicketsIssued e, MyOrderView current) =>
        current with { Status = nameof(OrderStatus.TicketsIssued) };

    public MyOrderView Apply(OrderConfirmed e, MyOrderView current) =>
        current with { Status = nameof(OrderStatus.Confirmed), ConfirmedAt = e.ConfirmedAt };

    public MyOrderView Apply(OrderCancelled e, MyOrderView current) =>
        current with { Status = nameof(OrderStatus.Cancelled), CancelledAt = e.CancelledAt };
}
