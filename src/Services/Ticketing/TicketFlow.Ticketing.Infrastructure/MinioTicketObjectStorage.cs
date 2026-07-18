using Minio;
using Minio.DataModel.Args;
using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Infrastructure;

/// <summary>
/// Commit 3 — no longer creates the bucket (that moved to RenderTicketPdfFunction, the only
/// writer left; see its EnsureBucketExistsAsync). This class is now read-only, backing the
/// ticket-download endpoint only.
/// </summary>
public sealed class MinioTicketObjectStorage(IMinioClient minioClient, string bucketName) : ITicketObjectStorage
{
    public async Task<byte[]> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        using var memoryStream = new MemoryStream();
        await minioClient.GetObjectAsync(
            new GetObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectKey)
                .WithCallbackStream(stream => stream.CopyTo(memoryStream)),
            cancellationToken);

        return memoryStream.ToArray();
    }
}
