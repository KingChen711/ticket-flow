using MassTransit;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Api.Consumers;

/// <summary>
/// Real pipeline (Phase 5 Commit 8; PDF rendering extracted to a Lambda in Phase 9 Commit 3):
/// render QR/PDF per seat via RenderTicketPdfFunction (which uploads to MinIO itself), persist,
/// publish the result. Same two-layer idempotency as Payment (Commit 7): business-key dedup by
/// OrderId here (GetByOrderIdAsync — a second physically distinct message for an already-issued
/// order replays the existing PDF object keys instead of re-rendering/re-uploading) + MassTransit
/// EF Core Inbox on this consumer's endpoint (wired in Program.cs) for the redelivered-same-message
/// case.
/// </summary>
public sealed class IssueTicketsConsumer(
    ITicketRepository repository,
    ITicketPdfRenderer renderer,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<IssueTicketsConsumer> logger) : IConsumer<IssueTickets>
{
    public async Task Consume(ConsumeContext<IssueTickets> context)
    {
        var message = context.Message;

        var existing = await repository.GetByOrderIdAsync(message.OrderId, context.CancellationToken);
        if (existing.Count > 0)
        {
            logger.LogInformation(
                "[ticketing] duplicate IssueTickets for order {OrderId} — replaying {Count} existing ticket(s), not re-issuing.",
                message.OrderId,
                existing.Count);

            await context.Publish(new TicketsIssued(
                message.OrderId,
                existing.Select(t => t.PdfObjectKey).ToList(),
                existing.Max(t => t.IssuedAt)));
            return;
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var pdfObjectKeys = new List<string>(message.SeatIds.Count);

            foreach (var seatId in message.SeatIds)
            {
                // No "tickets/" prefix — the bucket itself is already named "tickets" (Commit 1).
                // Doubling it up as a key prefix too was the exact scratch-script bug caught
                // (and NOT reproduced in production code) during Commit 3 verification.
                var objectKey = $"{message.OrderId}/{seatId}.pdf";

                // Deterministic, unsigned payload (OrderId:SeatId) — matches the DB's own unique
                // key for a ticket. No crypto/signing yet: gate-scanning verification is Phase 8
                // (see ITicketPdfRenderer's doc comment / phase-5-plan.md backlog).
                var qrPayload = $"{message.OrderId}:{seatId}";

                var ticket = Ticket.Issue(message.OrderId, seatId, qrPayload, objectKey, now);

                var pdfModel = new TicketPdfModel(
                    ticket.Id, message.OrderId, message.EventId, seatId, message.CustomerName, qrPayload, now);
                var storedKey = await renderer.RenderAndStoreAsync(pdfModel, objectKey, context.CancellationToken);

                repository.Add(ticket);
                pdfObjectKeys.Add(storedKey);
            }

            await unitOfWork.SaveChangesAsync(context.CancellationToken);
            await context.Publish(new TicketsIssued(message.OrderId, pdfObjectKeys, now));
        }
        catch (Exception ex)
        {
            // Nothing was saved (SaveChangesAsync only runs after the whole loop succeeds), so a
            // failure partway through a multi-seat order leaves no partial rows — clean to retry.
            logger.LogWarning(ex, "[ticketing] IssueTickets failed for order {OrderId}", message.OrderId);
            await context.Publish(new TicketingFailed(message.OrderId, ex.Message));
        }
    }
}
