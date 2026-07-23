using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Messaging;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Api.Consumers;

/// <summary>
/// Renders and stores one PDF per seat, then records the tickets (Phase 5 Commit 8; rendering
/// moved into a Lambda in Phase 9 Commit 3).
/// </summary>
/// <remarks>
/// <para>
/// Commit 10 changed two things that both cost real money when they went wrong.
/// </para>
/// <para>
/// <b>The claim.</b> The only previous guard was "does this order already have ticket rows?", which
/// is true only <em>after</em> a successful run — two deliveries arriving together both saw none
/// and both rendered and uploaded every PDF. A <see cref="TicketIssuanceJob"/> unique on OrderId is
/// now inserted and committed before any rendering starts, so the loser of that race is refused by
/// the database before it can reach the renderer.
/// </para>
/// <para>
/// <b>The failure classification.</b> The old <c>catch (Exception)</c> published
/// <c>TicketingFailed</c> for anything at all, so a MinIO hiccup or a Lambda cold-start timeout
/// told the saga to refund the customer and release their seats — for an order whose PDFs had very
/// possibly uploaded fine. Transient faults now propagate so the message is retried; only a refusal
/// that retrying cannot fix fails the order. Same distinction Commit 6 drew between a declined card
/// and a lost response.
/// </para>
/// </remarks>
public sealed class IssueTicketsConsumer(
    ITicketRepository repository,
    ITicketIssuanceJobRepository jobs,
    ITicketPdfRenderer renderer,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<IssueTicketsConsumer> logger) : IConsumer<IssueTickets>
{
    public async Task Consume(ConsumeContext<IssueTickets> context)
    {
        var message = context.Message;
        ContractVersionMetrics.RecordInbound(nameof(IssueTickets), message.PriceSnapshotVersion);

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

        var job = await jobs.GetByOrderAsync(message.OrderId, context.CancellationToken);
        if (job is { State: TicketIssuanceJobState.Failed })
        {
            // Already refused for good; re-running would only fail the same way.
            logger.LogWarning(
                "[ticketing] IssueTickets for order {OrderId} was already refused: {Reason}",
                message.OrderId,
                job.FailureReason);
            await context.Publish(new TicketingFailed(message.OrderId, job.FailureReason ?? "Issuance was refused."));
            return;
        }

        if (job is null)
        {
            job = TicketIssuanceJob.Claim(message.OrderId, message.EventId, message.UserId, timeProvider.GetUtcNow());
            jobs.Add(job);
            try
            {
                await unitOfWork.SaveChangesAsync(context.CancellationToken);
            }
            catch (Exception exception) when (jobs.IsUniqueViolation(exception))
            {
                // A concurrent delivery claimed this order first and owns the rendering. Throwing
                // rather than returning brings this message back once that one has finished, and
                // the replay branch above then answers it from the persisted tickets.
                TicketIssuanceMetrics.RecordRace();
                throw new OutOfOrderMessageException(
                    $"Order '{message.OrderId}' is already being issued by another delivery.");
            }
        }
        else
        {
            job.RecordAttempt();
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
                //
                // Deterministic in (OrderId, SeatId), so a retry overwrites the same object instead
                // of leaving an orphan behind — the storage write is idempotent by key even though
                // the renderer call itself is not.
                var objectKey = $"{message.OrderId}/{seatId}.pdf";

                // Deterministic, unsigned payload (OrderId:SeatId) — matches the DB's own unique
                // key for a ticket. No crypto/signing yet: see ITicketPdfRenderer's doc comment.
                var qrPayload = $"{message.OrderId}:{seatId}";

                var ticket = Ticket.Issue(
                    message.OrderId, seatId, qrPayload, objectKey, now, message.UserId, message.EventId);

                var pdfModel = new TicketPdfModel(
                    ticket.Id, message.OrderId, message.EventId, seatId, message.CustomerName, qrPayload, now);
                var storedKey = await renderer.RenderAndStoreAsync(pdfModel, objectKey, context.CancellationToken);

                repository.Add(ticket);
                pdfObjectKeys.Add(storedKey);
            }

            job.MarkIssued(now);
            await unitOfWork.SaveChangesAsync(context.CancellationToken);
            TicketIssuanceMetrics.RecordIssued(message.SeatIds.Count);
            await context.Publish(new TicketsIssued(message.OrderId, pdfObjectKeys, now));
        }
        catch (PermanentMessageFaultException exception)
        {
            // The only path that may fail the order. Everything else is left to redelivery.
            job.MarkFailed(exception.Message, timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(context.CancellationToken);
            TicketIssuanceMetrics.RecordFailed();

            logger.LogError(exception, "[ticketing] IssueTickets permanently refused for order {OrderId}", message.OrderId);
            await context.Publish(new TicketingFailed(message.OrderId, exception.Message));
        }
        catch (Exception exception)
        {
            // Transient: MinIO unreachable, a Lambda cold-start timeout, a dropped DB connection.
            // The job stays Pending so a retry resumes it, and the saga hears nothing — telling it
            // "ticketing failed" here would refund a customer whose PDFs are probably fine.
            TicketIssuanceMetrics.RecordTransientFailure();
            logger.LogWarning(
                exception,
                "[ticketing] IssueTickets for order {OrderId} hit a transient fault; leaving it for redelivery.",
                message.OrderId);
            throw;
        }
    }
}
