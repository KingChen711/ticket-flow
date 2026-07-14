using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Infrastructure;

/// <summary>
/// QuestPDF.Settings.License is set once at startup (Api Program.cs), not here — a
/// per-Render call assignment would be wasteful and is a global static setting anyway.
/// </summary>
public sealed class QuestPdfTicketRenderer : ITicketPdfRenderer
{
    public byte[] Render(TicketPdfModel model)
    {
        var qrPngBytes = GenerateQrPng(model.QrPayload);

        return Document.Create(container =>
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
                    // Catalog's event title/venue name (see ITicketPdfRenderer doc comment).
                    column.Item().Text($"Order: {model.OrderId}");
                    column.Item().Text($"Event: {model.EventId}");
                    column.Item().Text($"Seat: {model.SeatId}");
                    column.Item().Text($"Customer: {model.CustomerName}");
                    column.Item().Text($"Issued: {model.IssuedAt:u}");
                    column.Item().AlignCenter().Width(150).Image(qrPngBytes);
                });

                page.Footer().AlignCenter().Text($"Ticket {model.TicketId}").FontSize(8);
            });
        }).GeneratePdf();
    }

    private static byte[] GenerateQrPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var pngQr = new PngByteQRCode(qrData);
        return pngQr.GetGraphic(20);
    }
}
