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

        // RequireOrganizer directly at the gateway (not RequireAuthenticated + service-side
        // enforcement like every other write route) — there's no ASP.NET service on the other
        // end to own that check itself; the Lambda trusts the Docker network the same way every
        // other service-tier endpoint in this project does (docs/phase-9-plan.md mục 6).
        group.MapPost("/posters", UploadPosterAsync).RequireAuthorization("RequireOrganizer");

        // Anonymous — posters are public images, same as catalog's GET routes.
        group.MapGet("/{**key}", GetPosterAsync);
    }

    private static async Task<IResult> UploadPosterAsync(HttpRequest request, IAmazonLambda lambda, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);

        var payload = JsonSerializer.Serialize(new { base64Content = Convert.ToBase64String(buffer.ToArray()) }, JsonOptions);
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

    private sealed record UploadPosterResult(bool Accepted, string? Key, string? ThumbnailKey, string? Error);

    private sealed record GetPosterResult(bool Found, string? Base64Content, string? ContentType);
}
