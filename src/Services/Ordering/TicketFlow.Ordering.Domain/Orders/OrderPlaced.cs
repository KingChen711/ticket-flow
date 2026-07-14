namespace TicketFlow.Ordering.Domain.Orders;

/// <summary>
/// Plain record, NOT : TicketFlow.Domain.DomainEvent. That base type (EventId + OccurredOn) is
/// for the in-process "notify, then Ignore(DomainEvents) for EF" pattern used by state-based
/// services (Catalog/Identity/Reservation) — this event IS the persisted data, tracked by
/// Marten's own event envelope (its own id, timestamp, stream sequence, version). Inheriting
/// DomainEvent would just duplicate/conflict with that envelope's metadata for no benefit.
/// </summary>
public sealed record OrderPlaced(
    Guid OrderId,
    Guid CustomerId,
    Guid CatalogEventId,
    IReadOnlyList<string> SeatIds,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset PlacedAt);
