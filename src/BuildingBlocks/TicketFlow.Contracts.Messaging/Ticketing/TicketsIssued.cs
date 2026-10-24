namespace TicketFlow.Contracts.Messaging.Ticketing;

/// <summary>
/// Event published by the ticketing consumer after RenderTicketPdfFunction has successfully
/// generated and uploaded all QR/PDF tickets to MinIO (docs/phase-9-plan.md Commit 3). The
/// PurchaseSaga transitions
/// <c>AwaitingTickets → Completed</c>, appends the <c>TicketsIssued</c> Order domain event,
/// and then sends <c>NotifyOrderCompleted</c>.
/// </summary>
public sealed record TicketsIssued(
    Guid OrderId,
    IReadOnlyList<string> TicketPdfUrls,
    DateTimeOffset IssuedAt,
    string? OperationKey = null,
    long AggregateVersion = 0);
