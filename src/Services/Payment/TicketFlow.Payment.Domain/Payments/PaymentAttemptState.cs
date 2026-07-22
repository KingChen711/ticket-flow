namespace TicketFlow.Payment.Domain.Payments;

/// <summary>
/// Phase 12 Commit 6. <see cref="Unknown"/> is the state this whole commit exists for: the call
/// timed out, the circuit opened, or the response was lost, so the provider may or may not have
/// taken the customer's money. Collapsing that into "failed" — which the pre-Phase-12 code did by
/// catching every exception — releases the seats and cancels the order while the charge may still
/// be live. Unknown is resolved by a provider lookup, never by assumption.
/// </summary>
public enum PaymentAttemptState
{
    Pending,
    Authorized,
    Declined,
    Unknown,
}
