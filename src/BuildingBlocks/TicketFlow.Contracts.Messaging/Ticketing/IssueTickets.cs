namespace TicketFlow.Contracts.Messaging.Ticketing;

/// <summary>
/// Command sent by the PurchaseSaga after payment succeeds. The ticketing service consumer
/// generates a QR payload per seat, then invokes RenderTicketPdfFunction (Lambda,
/// docs/phase-9-plan.md Commit 3) to render the PDF and upload it to MinIO, and publishes
/// <see cref="TicketsIssued"/> or <see cref="TicketingFailed"/>.
/// </summary>
public sealed record IssueTickets(
    Guid OrderId,
    Guid UserId,
    Guid EventId,
    IReadOnlyList<Guid> SeatIds,
    string CustomerName,
    string CustomerEmail,
    string? OperationKey = null,
    long PriceSnapshotVersion = 0);
