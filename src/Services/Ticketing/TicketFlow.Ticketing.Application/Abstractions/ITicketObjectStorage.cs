namespace TicketFlow.Ticketing.Application.Abstractions;

/// <summary>
/// Separate from ITicketPdfRenderer — object storage is a genuinely distinct infrastructure
/// concern (could swap MinIO for S3/Azure Blob later without touching rendering logic).
/// Read-only since Commit 3: UploadAsync was deleted — the RenderTicketPdf Lambda now writes the
/// tickets bucket directly (its own MinIO client, not this port), so the only remaining caller of
/// this port is the ticket-download endpoint.
/// </summary>
public interface ITicketObjectStorage
{
    /// <summary>
    /// Phase 8 Commit 1 — streamed through the ticketing API rather than a MinIO presigned URL:
    /// a presigned URL generated inside the Docker network would carry the internal hostname
    /// (minio:9000), unresolvable from the browser — the same class of trap as Keycloak's
    /// issuer mismatch (Phase 7). Streaming through the API also gets auth for free.
    /// </summary>
    Task<byte[]> DownloadAsync(string objectKey, CancellationToken cancellationToken = default);
}
