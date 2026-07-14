namespace TicketFlow.Ordering.Application.Orders;

public sealed record OrderResponse(
    Guid OrderId,
    Guid CustomerId,
    Guid CatalogEventId,
    IReadOnlyList<Guid> SeatIds,
    decimal TotalAmount,
    string Currency,
    string Status);
