using Minio;
using Minio.DataModel.Args;
using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Infrastructure;

/// <summary>
/// Bucket is lazy-created here on first upload rather than via an init container (Commit 1
/// deferred this choice) — simplest option, no extra compose service, and idempotent
/// (BucketExistsAsync check first).
/// </summary>
public sealed class MinioTicketObjectStorage(IMinioClient minioClient, string bucketName) : ITicketObjectStorage
{
    public async Task<string> UploadAsync(
        string objectKey,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        await EnsureBucketExistsAsync(cancellationToken);

        using var stream = new MemoryStream(content);
        await minioClient.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectKey)
                .WithStreamData(stream)
                .WithObjectSize(content.Length)
                .WithContentType(contentType),
            cancellationToken);

        return objectKey;
    }

    private async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
    {
        var exists = await minioClient.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(bucketName),
            cancellationToken);

        if (!exists)
            await minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName), cancellationToken);
    }
}
