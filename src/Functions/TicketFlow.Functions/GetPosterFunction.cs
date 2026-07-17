using System.Net;
using Amazon.Lambda.Core;
using Amazon.S3;

namespace TicketFlow.Functions;

/// <summary>
/// Invoked synchronously by the gateway via the Lambda Invoke API — same reasoning as
/// UploadPosterFunction's doc comment (Function URLs ruled out: unstable per-deploy hostname).
/// No presigned URL either, same reasoning as ticketing's PDF download since Phase 8: a presigned
/// URL generated inside the Docker network would carry a hostname the browser can't resolve —
/// streaming the bytes back through the gateway avoids that trap entirely.
/// </summary>
public sealed class GetPosterFunction
{
    private const string BucketName = "posters";
    private static readonly IAmazonS3 S3Client = S3ClientFactory.Create();

    public async Task<GetPosterResponse> Handler(GetPosterRequest request, ILambdaContext context)
    {
        try
        {
            using var response = await S3Client.GetObjectAsync(BucketName, request.Key);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer);

            return new GetPosterResponse(
                Found: true,
                Base64Content: Convert.ToBase64String(buffer.ToArray()),
                ContentType: response.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new GetPosterResponse(Found: false, Base64Content: null, ContentType: null);
        }
    }
}

public sealed record GetPosterRequest(string Key);

public sealed record GetPosterResponse(bool Found, string? Base64Content, string? ContentType);
