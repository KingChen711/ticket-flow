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

    /// <summary>
    /// Phase 8 Commit 1 — streamed through the ticketing API rather than a MinIO presigned URL:
    /// a presigned URL generated inside the Docker network would carry the internal hostname
    /// (minio:9000), unresolvable from the browser — the same class of trap as Keycloak's
    /// issuer mismatch (Phase 7). Streaming through the API also gets auth for free.
    /// </summary>
    Task<byte[]> DownloadAsync(string objectKey, CancellationToken cancellationToken = default);
}
