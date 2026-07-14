namespace TicketFlow.Ordering.Domain.Orders;

public sealed record OrderCancelled(Guid OrderId, string Reason, DateTimeOffset CancelledAt);
