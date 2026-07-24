using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;

namespace TicketFlow.Functions;

/// <summary>
/// Invoked synchronously by the gateway via the Lambda Invoke API (AWSSDK.Lambda), not exposed
/// as a Function URL — real finding while wiring this (docs/phase-9-plan.md Commit 2, mục 3):
/// LocalStack generates a random subdomain per `create-function-url-config` call with no way to
/// pin it, and that ID doesn't survive a restart (Community has no persistence). A URL the
/// gateway's own config would need to hardcode can't be random and unstable, so the plan's other
/// risk-mitigation path won by default: invoke by function NAME, which is stable, same pattern
/// mục 4 already uses for ticketing -> RenderTicketPdf. Plain request/response DTOs here instead
/// of Amazon.Lambda.APIGatewayEvents — that package's shapes only earned their keep when this
/// was reachable as raw HTTP; a direct SDK Invoke call doesn't need an HTTP envelope.
///
/// Objects are written under "originals/" specifically so the S3 bucket notification driving
/// ThumbnailPosterFunction can be scoped to that prefix — writing a thumbnail into the same
/// bucket under any prefix ThumbnailPosterFunction also listens on would re-trigger itself.
/// </summary>
public sealed class UploadPosterFunction
{
    private const string BucketName = "posters";
    private static readonly IAmazonS3 S3Client = S3ClientFactory.Create();

    public async Task<UploadPosterResponse> Handler(UploadPosterRequest request, ILambdaContext context)
    {
        var bytes = Convert.FromBase64String(request.Base64Content);

        var extension = ImageFormat.DetectExtension(bytes);
        if (extension is null)
        {
            context.Logger.LogWarning($"UploadPoster rejected {bytes.Length} bytes — not a recognized PNG/JPEG.");
            return new UploadPosterResponse(Accepted: false, Key: null, ThumbnailKey: null, Error: "Uploaded file is not a recognized image (PNG or JPEG).");
        }

        // Commit 14: being a real PNG is not the same as being safe to decode. Checked here, before
        // the object is stored, so a bomb never reaches S3 and therefore never triggers the
        // thumbnail function that would decode it.
        if (!ImageLimits.TryValidate(bytes, out var limitError))
        {
            context.Logger.LogWarning($"UploadPoster rejected {bytes.Length} bytes — {limitError}");
            return new UploadPosterResponse(Accepted: false, Key: null, ThumbnailKey: null, Error: limitError);
        }

        var key = $"originals/{Guid.NewGuid():N}.{extension}";
        await S3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = BucketName,
            Key = key,
            InputStream = new MemoryStream(bytes),
            ContentType = ImageFormat.ContentType(extension),
        });

        context.Logger.LogInformation($"UploadPoster stored {key} ({bytes.Length} bytes)");
        return new UploadPosterResponse(Accepted: true, Key: key, ThumbnailKey: key.Replace("originals/", "thumbs/"), Error: null);
    }
}

public sealed record UploadPosterRequest(string Base64Content);

public sealed record UploadPosterResponse(bool Accepted, string? Key, string? ThumbnailKey, string? Error);
