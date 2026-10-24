using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace TicketFlow.Gateway;

/// <summary>
/// Thin proxy to the poster Lambdas (docs/phase-9-plan.md Commit 2) — invoked via the Lambda
/// Invoke API by stable function name, not routed as raw HTTP through YARP. Real finding while
/// wiring this: LocalStack's Function URLs get a random subdomain per deploy with no way to pin
/// it, and Community has no persistence across restarts — a hostname the Gateway's own config
/// would need to hardcode can't be random. Function names are stable, so SDK Invoke is what
/// actually works here (same pattern Ticketing uses for RenderTicketPdf in Commit 3).
/// </summary>
public static class MediaEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapMediaEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/media").WithTags("Media");

        // The role check only establishes that this is an organizer. UploadPosterAsync separately
        // asks Catalog whether this caller owns the specific Event before any bytes are accepted.
        group.MapPost("/events/{eventId:guid}/posters", UploadPosterAsync)
            .RequireAuthorization("RequireOrganizer");

        // Anonymous — posters are public images, same as catalog's GET routes.
        group.MapGet("/{**key}", GetPosterAsync);
    }

    /// <summary>
    /// Matches ImageLimits.MaxBytes in the Lambda. Four MiB leaves safe room for base64 expansion
    /// and the JSON envelope below the six-MiB synchronous AWS Lambda Invoke payload limit.
    /// Enforced here as well, not instead: this is where buffering/base64 expansion happens.
    /// </summary>
    private const long MaxUploadBytes = 4 * 1024 * 1024;

    private static async Task<IResult> UploadPosterAsync(
        Guid eventId,
        HttpRequest request,
        IHttpClientFactory httpClientFactory,
        IAmazonLambda lambda,
        CancellationToken ct)
    {
        var ownershipFailure = await AuthorizeEventAsync(eventId, request, httpClientFactory, ct);
        if (ownershipFailure is not null)
            return ownershipFailure;

        // Content-Length is a fast reject when it is present and honest; the bounded read below is
        // what actually enforces the limit, because a chunked request carries no length at all and
        // a lying one is exactly what an attacker sends.
        if (request.ContentLength > MaxUploadBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var buffer = new MemoryStream();
        if (!await CopyBoundedAsync(request.Body, buffer, MaxUploadBytes, ct))
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var payload = JsonSerializer.Serialize(
            new { eventId, base64Content = Convert.ToBase64String(buffer.ToArray()) },
            JsonOptions);
        var invokeResponse = await lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = "UploadPosterFunction",
            InvocationType = InvocationType.RequestResponse,
            Payload = payload,
        }, ct);

        using var reader = new StreamReader(invokeResponse.Payload);
        var responseJson = await reader.ReadToEndAsync(ct);

        if (invokeResponse.FunctionError is not null)
        {
            return Results.Problem($"UploadPosterFunction failed: {responseJson}", statusCode: 502);
        }

        var result = JsonSerializer.Deserialize<UploadPosterResult>(responseJson, JsonOptions)!;
        return result.Accepted
            ? Results.Created($"/media/{result.Key}", new { result.Key, result.ThumbnailKey })
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult?> AuthorizeEventAsync(
        Guid eventId,
        HttpRequest request,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        using var authorizationRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"events/{eventId}/media-upload-authorization");
        authorizationRequest.Headers.TryAddWithoutValidation(
            "Authorization",
            request.Headers.Authorization.ToString());

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory
                .CreateClient("catalog-media-ownership")
                .SendAsync(authorizationRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;

            var status = (int)response.StatusCode;
            return status is StatusCodes.Status400BadRequest
                or StatusCodes.Status401Unauthorized
                or StatusCodes.Status403Forbidden
                or StatusCodes.Status404NotFound
                ? Results.StatusCode(status)
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetPosterAsync(string key, IAmazonLambda lambda, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { key }, JsonOptions);
        var invokeResponse = await lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = "GetPosterFunction",
            InvocationType = InvocationType.RequestResponse,
            Payload = payload,
        }, ct);

        using var reader = new StreamReader(invokeResponse.Payload);
        var responseJson = await reader.ReadToEndAsync(ct);

        if (invokeResponse.FunctionError is not null)
        {
            return Results.Problem($"GetPosterFunction failed: {responseJson}", statusCode: 502);
        }

        var result = JsonSerializer.Deserialize<GetPosterResult>(responseJson, JsonOptions)!;
        if (!result.Found)
        {
            return Results.NotFound();
        }

        var bytes = Convert.FromBase64String(result.Base64Content!);
        return Results.File(bytes, result.ContentType ?? "application/octet-stream");
    }

    /// <summary>
    /// Copies at most <paramref name="limit"/> bytes and reports whether the source stayed within
    /// it. Reads one byte past the limit on purpose — that is the only way to distinguish "exactly
    /// at the limit" from "more is coming", and stopping at the limit would silently truncate an
    /// oversized upload into a valid-looking one.
    /// </summary>
    private static async Task<bool> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long limit,
        CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                return true;

            total += read;
            if (total > limit)
                return false;

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed record UploadPosterResult(bool Accepted, string? Key, string? ThumbnailKey, string? Error);

    private sealed record GetPosterResult(bool Found, string? Base64Content, string? ContentType);
}
