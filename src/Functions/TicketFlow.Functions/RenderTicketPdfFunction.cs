using Amazon.Lambda.Core;
using Minio;
using Minio.DataModel.Args;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace TicketFlow.Functions;

/// <summary>
/// Extracted from Ticketing.Infrastructure's QuestPdfTicketRenderer (docs/phase-9-plan.md Commit
/// 3, mục 4) — same QuestPDF/QRCoder composition, now also doing the MinIO upload the caller
/// (IssueTicketsConsumer) used to do itself via ITicketObjectStorage. Bytes never round-trip back
/// over the Lambda Invoke API, only the objectKey does (ITicketPdfRenderer's doc comment explains
/// why) — this is also why ITicketObjectStorage.UploadAsync was deleted as dead code rather than
/// kept "just in case": this function is now the only writer of the tickets bucket.
///
/// Invoked synchronously via the Lambda Invoke API (AWSSDK.Lambda) by ticketing's
/// LambdaTicketRenderClient — same pattern as UploadPosterFunction/GetPosterFunction (Commit 2),
/// no Function URL, no HTTP envelope type.
/// </summary>
public sealed class RenderTicketPdfFunction
{
    private static readonly IMinioClient MinioClient = MinioClientFactory.Create();
    private static readonly string BucketName = Environment.GetEnvironmentVariable("MINIO_BUCKET_NAME") ?? "tickets";

    static RenderTicketPdfFunction()
    {
        // QuestPDF Community license — same free-tier term Ticketing.Api's Program.cs used to set
        // before this rendering moved here (Directory.Packages.props comment). A static
        // constructor runs once per Lambda execution environment, matching QuestPDF's own
        // requirement that this is a global setting, not a per-render one.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<RenderTicketPdfResponse> Handler(RenderTicketPdfRequest request, ILambdaContext context)
    {
        var qrPngBytes = GenerateQrPng(request.QrPayload);

        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A5);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(12));

                page.Header().Text("TicketFlow — Ve dien tu").FontSize(18).Bold();

                page.Content().Column(column =>
                {
                    column.Spacing(10);
                    // Only raw ids shown (EventId, SeatId) — Ticketing has no access to
                    // Catalog's event title/venue name (see ITicketPdfRenderer's doc comment).
                    column.Item().Text($"Order: {request.OrderId}");
                    column.Item().Text($"Event: {request.EventId}");
                    column.Item().Text($"Seat: {request.SeatId}");
                    column.Item().Text($"Customer: {request.CustomerName}");
                    column.Item().Text($"Issued: {request.IssuedAt:u}");
                    column.Item().AlignCenter().Width(150).Image(qrPngBytes);
                });

                page.Footer().AlignCenter().Text($"Ticket {request.TicketId}").FontSize(8);
            });
        }).GeneratePdf();

        await EnsureBucketExistsAsync();

        await MinioClient.PutObjectAsync(new PutObjectArgs()
            .WithBucket(BucketName)
            .WithObject(request.ObjectKey)
            .WithStreamData(new MemoryStream(pdfBytes))
            .WithObjectSize(pdfBytes.Length)
            .WithContentType("application/pdf"));

        context.Logger.LogInformation($"RenderTicketPdf: {request.ObjectKey} ({pdfBytes.Length} bytes) -> MinIO bucket '{BucketName}'");

        return new RenderTicketPdfResponse(request.ObjectKey);
    }

    // Same lazy-create-on-first-write behavior MinioTicketObjectStorage used to provide
    // (Phase 5 Commit 1 doc comment) — this function is now the bucket's only writer, so the
    // responsibility moved here rather than staying stranded in code nothing calls anymore.
    private static async Task EnsureBucketExistsAsync()
    {
        var exists = await MinioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(BucketName));
        if (!exists)
        {
            await MinioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(BucketName));
        }
    }

    private static byte[] GenerateQrPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var pngQr = new PngByteQRCode(qrData);
        return pngQr.GetGraphic(20);
    }
}

public sealed record RenderTicketPdfRequest(
    Guid TicketId,
    Guid OrderId,
    Guid EventId,
    Guid SeatId,
    string CustomerName,
    string QrPayload,
    DateTimeOffset IssuedAt,
    string ObjectKey);

public sealed record RenderTicketPdfResponse(string ObjectKey);
