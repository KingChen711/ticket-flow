namespace TicketFlow.Ticketing.Application.Abstractions;

/// <summary>
/// Application knows it needs "a PDF with this seat's QR embedded", not HOW (QRCoder + QuestPDF
/// in Infrastructure). Rendering intentionally bundles QR generation + PDF composition into one
/// operation — the QR image only exists to be embedded in the PDF, splitting them into two
/// separate ports would add indirection with no real boundary behind it.
///
/// Note: the rendered ticket only shows raw IDs (EventId, SeatId), not a friendly event
/// title/venue name — Ticketing has no access to Catalog's data (no cross-service DB read, and
/// IssueTickets doesn't carry it). A real limitation, not an oversight — see phase-5-plan.md
/// backlog.
/// </summary>
public interface ITicketPdfRenderer
{
    byte[] Render(TicketPdfModel model);
}

public sealed record TicketPdfModel(
    Guid TicketId,
    Guid OrderId,
    Guid EventId,
    Guid SeatId,
    string CustomerName,
    string QrPayload,
    DateTimeOffset IssuedAt);
