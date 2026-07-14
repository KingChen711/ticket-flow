namespace TicketFlow.Contracts.Messaging.Ticketing;

/// <summary>
/// Command sent by the PurchaseSaga after payment succeeds. The ticketing service consumer
/// generates a QR code per seat, renders a PDF ticket, uploads it to MinIO, and publishes
/// <see cref="TicketsIssued"/> or <see cref="TicketingFailed"/>.
/// </summary>
public sealed record IssueTickets(
    Guid OrderId,
    Guid UserId,
    Guid EventId,
    IReadOnlyList<Guid> SeatIds,
    string CustomerName,
    string CustomerEmail);
