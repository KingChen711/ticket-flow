namespace TicketFlow.Ticketing.Application.Abstractions;

/// <summary>
/// Separate from ITicketPdfRenderer — object storage is a genuinely distinct infrastructure
/// concern (could swap MinIO for S3/Azure Blob later without touching rendering logic).
/// </summary>
public interface ITicketObjectStorage
{
    /// <returns>The object key the PDF was stored under (not a full URL — Phase 5 doesn't yet
    /// expose a public/presigned URL scheme; see phase-5-plan.md backlog).</returns>
    Task<string> UploadAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken = default);
}
