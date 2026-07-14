namespace TicketFlow.Contracts.Messaging.Ticketing;

/// <summary>
/// Event published by the ticketing consumer when ticket generation or MinIO upload fails
/// (e.g., MinIO unreachable, PDF rendering error). The PurchaseSaga transitions
/// <c>AwaitingTickets → Compensating_Refund</c> and sends <c>RefundPayment</c>.
/// </summary>
public sealed record TicketingFailed(
    Guid OrderId,
    string Reason);
