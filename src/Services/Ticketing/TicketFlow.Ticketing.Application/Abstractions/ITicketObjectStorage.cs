namespace TicketFlow.Ticketing.Application.Abstractions;

/// <summary>
/// Separate from ITicketPdfRenderer — object storage is a genuinely distinct infrastructure
/// concern (could swap MinIO for S3/Azure Blob later without touching rendering logic).
/// UploadAsync remains intentionally absent: the RenderTicketPdf Lambda is the sole writer.
/// Ticketing reads customer PDFs and gives the reconciliation worker narrowly scoped list/delete
/// operations so failed renders do not leave unreferenced personal artifacts indefinitely.
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

    /// <summary>Lists artifact keys below an order-owned prefix for orphan reconciliation.</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(
        string prefix,
        CancellationToken cancellationToken = default);

    /// <summary>Idempotently removes one unreferenced artifact.</summary>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}
