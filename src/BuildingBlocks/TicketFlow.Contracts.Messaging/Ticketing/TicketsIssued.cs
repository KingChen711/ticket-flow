namespace TicketFlow.Contracts.Messaging.Ticketing;

/// <summary>
/// Event published by the ticketing consumer after successfully generating all QR/PDF tickets
/// and uploading them to MinIO. The PurchaseSaga transitions
/// <c>AwaitingTickets → Completed</c>, appends the <c>TicketsIssued</c> Order domain event,
/// and then sends <c>NotifyOrderCompleted</c>.
/// </summary>
public sealed record TicketsIssued(
    Guid OrderId,
    IReadOnlyList<string> TicketPdfUrls,
    DateTimeOffset IssuedAt);
