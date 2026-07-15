namespace TicketFlow.Ordering.Domain.Orders;

public enum OrderStatus
{
    Pending,
    SeatsConfirmed,
    PaymentAuthorized,
    TicketsIssued,
    Confirmed,
    // Compensation milestones (Commit 9) — transient, on the way to Cancelled.
    Refunded,
    SeatsReleased,
    Cancelled
}
