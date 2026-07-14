namespace TicketFlow.Ordering.Domain.Orders;

public sealed record OrderConfirmed(Guid OrderId, DateTimeOffset ConfirmedAt);
