namespace TicketFlow.Ticketing.Application.Abstractions;

/// <summary>
/// Application knows it needs "a PDF with this seat's QR embedded, stored somewhere retrievable
/// by objectKey", not HOW (QuestPDF/QRCoder + the MinIO upload, both now living in the
/// RenderTicketPdf Lambda — docs/phase-9-plan.md Commit 3). Rendering intentionally bundles QR
/// generation + PDF composition + storage into one operation — the bytes never need to round-trip
/// back over the Invoke API, only the objectKey the caller already chose does.
///
/// Note: the rendered ticket only shows raw IDs (EventId, SeatId), not a friendly event
/// title/venue name — Ticketing has no access to Catalog's data (no cross-service DB read, and
/// IssueTickets doesn't carry it). A real limitation, not an oversight — see phase-5-plan.md
/// backlog.
/// </summary>
public interface ITicketPdfRenderer
{
    /// <returns>The object key the PDF was stored under — same value as <paramref name="objectKey"/>,
    /// echoed back rather than assumed so a caller never has to trust an out-of-band contract.</returns>
    Task<string> RenderAndStoreAsync(TicketPdfModel model, string objectKey, CancellationToken cancellationToken = default);
}

public sealed record TicketPdfModel(
    Guid TicketId,
    Guid OrderId,
    Guid EventId,
    Guid SeatId,
    string CustomerName,
    string QrPayload,
    DateTimeOffset IssuedAt);
