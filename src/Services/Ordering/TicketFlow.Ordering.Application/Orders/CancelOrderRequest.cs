namespace TicketFlow.Ordering.Application.Orders;

/// <summary>
/// Separate from CancelOrderCommand — the command's OrderId comes from the route, not the
/// request body, so binding the whole command directly (as PlaceOrderCommand can be, since all
/// of it comes from the body) would double up on the id.
/// </summary>
public sealed record CancelOrderRequest(string Reason);
