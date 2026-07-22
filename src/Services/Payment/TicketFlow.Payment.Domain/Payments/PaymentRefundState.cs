namespace TicketFlow.Payment.Domain.Payments;

/// <summary>Mirror of <see cref="PaymentAttemptState"/> for the reverse direction. A refund whose
/// outcome is unknown must not be reported as completed to the saga.</summary>
public enum PaymentRefundState
{
    Pending,
    Refunded,
    Declined,
    Unknown,
}
