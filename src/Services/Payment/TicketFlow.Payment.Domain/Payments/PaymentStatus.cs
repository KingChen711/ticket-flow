namespace TicketFlow.Payment.Domain.Payments;

public enum PaymentStatus
{
    Pending,
    Succeeded,
    Failed,
    Refunded
}
