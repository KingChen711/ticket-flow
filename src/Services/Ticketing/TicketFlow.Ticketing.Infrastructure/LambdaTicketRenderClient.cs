using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Infrastructure;

/// <summary>
/// Replaces QuestPdfTicketRenderer (deleted, docs/phase-9-plan.md Commit 3) — rendering now lives
/// in the RenderTicketPdf Lambda, invoked by stable function name via the Lambda Invoke API. Same
/// pattern Gateway's MediaEndpoints.cs already uses for the poster Lambdas (Commit 2): invoke by
/// name because LocalStack Function URLs are unstable across restarts, not a raw HTTP call.
/// Synchronous RequestResponse invoke, not fire-and-forget — the plan's mục 4 explains why:
/// changing this to async would change OrderCompleted's semantics (an order can currently only be
/// Confirmed once its PDF object key exists), and a Lambda failure here throws, which MassTransit's
/// existing retry policy already handles the same way any other consumer failure does.
/// </summary>
public sealed class LambdaTicketRenderClient(IAmazonLambda lambda) : ITicketPdfRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> RenderAndStoreAsync(TicketPdfModel model, string objectKey, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                model.TicketId,
                model.OrderId,
                model.EventId,
                model.SeatId,
                model.CustomerName,
                model.QrPayload,
                model.IssuedAt,
                ObjectKey = objectKey,
            },
            JsonOptions);

        var invokeResponse = await lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = "RenderTicketPdfFunction",
            InvocationType = InvocationType.RequestResponse,
            Payload = payload,
        }, cancellationToken);

        using var reader = new StreamReader(invokeResponse.Payload);
        var responseJson = await reader.ReadToEndAsync(cancellationToken);

        if (invokeResponse.FunctionError is not null)
        {
            throw new InvalidOperationException($"RenderTicketPdfFunction failed: {responseJson}");
        }

        var result = JsonSerializer.Deserialize<RenderTicketPdfResult>(responseJson, JsonOptions)!;
        return result.ObjectKey;
    }

    private sealed record RenderTicketPdfResult(string ObjectKey);
}
