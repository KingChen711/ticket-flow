using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Orders;

internal static class OrderResponseMapper
{
    public static OrderResponse Map(Order order) => new(
        order.Id,
        order.CustomerId,
        order.CatalogEventId,
        order.SeatIds,
        order.Total.Amount,
        order.Total.Currency,
        order.Status.ToString());
}
