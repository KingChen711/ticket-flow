using Minio;
using Minio.DataModel.Args;
using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Infrastructure;

/// <summary>
/// Commit 3 — no longer creates the bucket (that moved to RenderTicketPdfFunction, the only
/// writer left; see its EnsureBucketExistsAsync). Ticketing reads customer PDFs and may list/delete
/// only deterministic order prefixes while reconciling abandoned issuance attempts.
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

    public async Task<IReadOnlyList<string>> ListKeysAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        var args = new ListObjectsArgs()
            .WithBucket(bucketName)
            .WithPrefix(prefix)
            .WithRecursive(true);

        await foreach (var item in minioClient.ListObjectsEnumAsync(args, cancellationToken))
            keys.Add(item.Key);

        return keys;
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) =>
        minioClient.RemoveObjectAsync(
            new RemoveObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectKey),
            cancellationToken);
}
