namespace TicketFlow.Contracts.Messaging.Payment;

/// <summary>
/// Event published by the payment consumer when charging fails (gateway rejection, Polly
/// circuit open, or all retries exhausted). The PurchaseSaga transitions
/// <c>AwaitingPayment → Compensating_Seats</c> and sends <c>ReleaseSeats</c>.
/// </summary>
public sealed record PaymentFailed(
    Guid OrderId,
    string Reason,
    string? OperationKey = null,
    long AggregateVersion = 0);
