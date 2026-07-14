using TicketFlow.Domain;

namespace TicketFlow.Ordering.Domain.Orders;

/// <summary>
/// Event-sourced aggregate: state is the RESULT of folding events (Apply), not data written
/// directly. Deliberately extends Entity&lt;Guid&gt; (identity + equality), not
/// AggregateRoot&lt;Guid&gt; — AggregateRoot's Raise()/DomainEvents/ClearDomainEvents machinery
/// exists for the state-based "buffer then dispatch via outbox" pattern (Catalog/Reservation);
/// mixing it in here would be a second, redundant, and confusing way to track "new events",
/// alongside the actual event stream Marten owns. The only mutator is Apply — Confirm/Cancel are
/// pure decisions that return an event without mutating, so there is exactly one place state
/// changes (Apply), whether driven by Marten's fold-on-load or by a freshly-decided event.
/// </summary>
public sealed class Order : Entity<Guid>
{
    public Guid CustomerId { get; private set; }
    public Guid CatalogEventId { get; private set; }
    public IReadOnlyList<string> SeatIds { get; private set; } = [];
    public Money Total { get; private set; } = null!;
    public OrderStatus Status { get; private set; }

    // Required for Marten to materialize an aggregate before folding events onto it.
    private Order() { }

    /// <summary>
    /// Reconstructs an aggregate by folding its full event history — the same operation Marten's
    /// AggregateStreamAsync performs (via reflection over the Apply overloads) when loading an
    /// order for a command, and what tests use to build an Order without touching Postgres.
    /// </summary>
    public static Order Load(IEnumerable<object> events)
    {
        var order = new Order();
        foreach (var e in events)
            order.ApplyEvent(e);

        return order;
    }

    private void ApplyEvent(object e)
    {
        switch (e)
        {
            case OrderPlaced placed: Apply(placed); break;
            case OrderConfirmed confirmed: Apply(confirmed); break;
            case OrderCancelled cancelled: Apply(cancelled); break;
            default: throw new InvalidOperationException($"Order cannot apply event of type {e.GetType()}.");
        }
    }

    /// <summary>
    /// No aggregate exists yet to call an instance method on — this decides whether placing the
    /// order is valid and returns the event that, once appended as the first event of a new
    /// stream, brings the aggregate into existence (via Apply).
    /// </summary>
    public static OrderPlaced Place(
        Guid customerId,
        Guid catalogEventId,
        IReadOnlyList<string> seatIds,
        Money total,
        DateTimeOffset now)
    {
        if (seatIds is not { Count: > 0 })
            throw new ArgumentException("An order must include at least one seat.", nameof(seatIds));

        return new OrderPlaced(Guid.NewGuid(), customerId, catalogEventId, seatIds, total.Amount, total.Currency, now);
    }

    public OrderConfirmed Confirm(DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Only a pending order can be confirmed (current status: {Status}).");

        return new OrderConfirmed(Id, now);
    }

    public OrderCancelled Cancel(string reason, DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Only a pending order can be cancelled (current status: {Status}).");

        return new OrderCancelled(Id, Guard.NotNullOrEmpty(reason, nameof(reason)), now);
    }

    public void Apply(OrderPlaced e)
    {
        Id = e.OrderId;
        CustomerId = e.CustomerId;
        CatalogEventId = e.CatalogEventId;
        SeatIds = e.SeatIds;
        Total = Money.Create(e.TotalAmount, e.Currency);
        Status = OrderStatus.Pending;
    }

    public void Apply(OrderConfirmed e) => Status = OrderStatus.Confirmed;

    public void Apply(OrderCancelled e) => Status = OrderStatus.Cancelled;
}
