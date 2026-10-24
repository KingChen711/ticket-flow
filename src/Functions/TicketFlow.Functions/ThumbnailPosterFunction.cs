using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Model;
using SkiaSharp;

namespace TicketFlow.Functions;

/// <summary>
/// S3 event-triggered (docs/phase-9-plan.md Commit 2) — the actual "serverless" trigger this
/// commit exists to demonstrate, not another Function URL. The bucket notification (wired in the
/// init script) is scoped to the "originals/" prefix specifically so writing a thumbnail back
/// into the same bucket under "thumbs/" can't re-trigger this same function — no in-handler
/// "skip if already a thumbnail" check needed, the notification filter is the single source of
/// truth for what triggers this.
/// </summary>
public sealed class ThumbnailPosterFunction
{
    private const int MaxWidth = 200;
    private static readonly IAmazonS3 S3Client = S3ClientFactory.Create();

    public async Task<string> Handler(S3Event s3Event, ILambdaContext context)
    {
        foreach (var record in s3Event.Records)
        {
            var bucket = record.S3.Bucket.Name;
            var key = Uri.UnescapeDataString(record.S3.Object.Key);

            using var getResponse = await S3Client.GetObjectAsync(bucket, key);
            using var input = new MemoryStream();
            await getResponse.ResponseStream.CopyToAsync(input);
            input.Position = 0;

            // Commit 14: re-checked here rather than trusted from the upload path. This function is
            // triggered by an S3 notification, so its real input is "whatever is in the bucket
            // under originals/", not "whatever UploadPosterFunction accepted" — anything else that
            // can write to that prefix would otherwise reach SKBitmap.Decode unguarded.
            if (!ImageLimits.TryValidate(input.ToArray(), out var limitError))
            {
                context.Logger.LogWarning($"ThumbnailPoster: refusing to decode {key} — {limitError}");
                continue;
            }

            input.Position = 0;
            using var original = SKBitmap.Decode(input);
            if (original is null)
            {
                context.Logger.LogWarning($"ThumbnailPoster: could not decode {key} as an image, skipping");
                continue;
            }

            var scale = (float)MaxWidth / original.Width;
            var thumbWidth = MaxWidth;
            var thumbHeight = Math.Max(1, (int)(original.Height * scale));

            using var thumbnail = original.Resize(new SKImageInfo(thumbWidth, thumbHeight), SKSamplingOptions.Default)
                ?? throw new InvalidOperationException($"SKBitmap.Resize returned null for {key}");
            using var image = SKImage.FromBitmap(thumbnail);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);

            var thumbKey = key.Replace("originals/", "thumbs/");
            await S3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = thumbKey,
                InputStream = encoded.AsStream(),
                ContentType = "image/png",
            });

            context.Logger.LogInformation($"ThumbnailPoster: {key} ({original.Width}x{original.Height}) -> {thumbKey} ({thumbWidth}x{thumbHeight})");
        }

        return "OK";
    }
}
