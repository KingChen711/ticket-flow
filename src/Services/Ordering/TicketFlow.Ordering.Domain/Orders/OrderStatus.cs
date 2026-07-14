namespace TicketFlow.Ordering.Domain.Orders;

public enum OrderStatus
{
    Pending,
    SeatsConfirmed,
    PaymentAuthorized,
    TicketsIssued,
    Confirmed,
    Cancelled
}
