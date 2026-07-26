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

        var job = await jobs.GetByOrderAsync(message.OrderId, context.CancellationToken);
        if (job is { State: TicketIssuanceJobState.Cancelled })
        {
            logger.LogInformation(
                "[ticketing] ignoring late IssueTickets for cancelled order {OrderId}; cancellation tombstone is durable.",
                message.OrderId);
            return;
        }

        if (job is { State: TicketIssuanceJobState.Failed })
        {
            // A failed job wins over any rows left by an older, partially-committed issuer. Checking
            // for "at least one ticket" first would turn an incomplete batch into TicketsIssued on
            // a semantic redelivery, after the saga had already been told to compensate the order.
            logger.LogWarning(
                "[ticketing] IssueTickets for order {OrderId} was already refused: {Reason}",
                message.OrderId,
                job.FailureReason);
            await context.Publish(new TicketingFailed(message.OrderId, job.FailureReason ?? "Issuance was refused."));
            return;
        }

        var expectedSeatIds = message.SeatIds.ToHashSet();
        if (message.OrderId == Guid.Empty ||
            message.EventId == Guid.Empty ||
            message.UserId == Guid.Empty ||
            expectedSeatIds.Count == 0 ||
            expectedSeatIds.Contains(Guid.Empty) ||
            expectedSeatIds.Count != message.SeatIds.Count ||
            string.IsNullOrWhiteSpace(message.CustomerName) ||
            message.CustomerName.Length > 200 ||
            string.IsNullOrWhiteSpace(message.CustomerEmail) ||
            message.CustomerEmail.Length > 320 ||
            message.OperationKey?.Length > 500)
        {
            throw new PermanentMessageFaultException(
                $"Order '{message.OrderId}' has an invalid ticket issuance command.");
        }

        var attemptAt = timeProvider.GetUtcNow();
        if (job is not null)
        {
            if (!job.TryCaptureOrMatchIntent(
                    message.EventId,
                    message.UserId,
                    message.SeatIds,
                    message.CustomerName,
                    message.CustomerEmail,
                    message.OperationKey,
                    message.PriceSnapshotVersion))
            {
                throw new PermanentMessageFaultException(
                    $"Order '{message.OrderId}' was redelivered with a different issuance intent.");
            }

            job.RecordAttempt(attemptAt);
        }

        var existing = await repository.GetByOrderIdAsync(message.OrderId, context.CancellationToken);
        var existingBySeat = existing.ToDictionary(ticket => ticket.SeatId);
        if (existing.Count > 0 &&
            (existingBySeat.Keys.Any(seatId => !expectedSeatIds.Contains(seatId)) ||
             existing.Any(ticket => ticket.EventId != message.EventId || ticket.CustomerId != message.UserId)))
        {
            // Same OrderId with a different seat/customer/event tuple is a semantic collision, not
            // a harmless broker duplicate. Do not publish success from somebody else's rows and do
            // not mutate the durable job until an operator reconciles the conflicting intent.
            throw new PermanentMessageFaultException(
                $"Order '{message.OrderId}' already has tickets that do not match the issuance command.");
        }

        if (existingBySeat.Count == expectedSeatIds.Count &&
            expectedSeatIds.All(existingBySeat.ContainsKey))
        {
            logger.LogInformation(
                "[ticketing] duplicate IssueTickets for order {OrderId} — replaying {Count} existing ticket(s), not re-issuing.",
                message.OrderId,
                existing.Count);

            // Captures recovery metadata on legacy issuance-job rows before replaying success.
            if (job is not null)
            {
                if (job.State != TicketIssuanceJobState.Issued)
                    job.MarkIssued(attemptAt);
                await unitOfWork.SaveChangesAsync(context.CancellationToken);
            }

            await context.Publish(new TicketsIssued(
                message.OrderId,
                message.SeatIds.Select(seatId => existingBySeat[seatId].PdfObjectKey).ToList(),
                existing.Max(t => t.IssuedAt)));
            return;
        }

        if (job is null)
        {
            job = TicketIssuanceJob.Claim(
                message.OrderId,
                message.EventId,
                message.UserId,
                message.SeatIds,
                message.CustomerName,
                message.CustomerEmail,
                message.OperationKey,
                message.PriceSnapshotVersion,
                attemptAt);
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
            // Attempts and the full command snapshot must survive a renderer crash; otherwise a
            // worker could find a Pending row but have no durable command to reconstruct.
            await unitOfWork.SaveChangesAsync(context.CancellationToken);
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var pdfObjectKeys = new List<string>(message.SeatIds.Count);
            var renderedTickets = new List<Ticket>(message.SeatIds.Count - existingBySeat.Count);

            foreach (var seatId in message.SeatIds)
            {
                if (existingBySeat.TryGetValue(seatId, out var existingTicket))
                {
                    pdfObjectKeys.Add(existingTicket.PdfObjectKey);
                    continue;
                }

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

                // Do not attach a row to the EF change tracker until every missing artifact has
                // rendered. A permanent failure on seat N must not make SaveChanges below persist
                // seats 1..N-1 together with a Failed job.
                renderedTickets.Add(ticket);
                pdfObjectKeys.Add(storedKey);
            }

            foreach (var ticket in renderedTickets)
                repository.Add(ticket);

            job.MarkIssued(now);
            await unitOfWork.SaveChangesAsync(context.CancellationToken);
            TicketIssuanceMetrics.RecordIssued(message.SeatIds.Count);
            await context.Publish(new TicketsIssued(message.OrderId, pdfObjectKeys, now));
        }
        catch (PermanentMessageFaultException exception)
        {
            // The only path that may fail the order. Everything else is left to redelivery.
            var failedAt = timeProvider.GetUtcNow();
            job.MarkFailed(exception.Message, failedAt);
            // A legacy deployment may already have committed a subset before failing a later
            // seat. Fence those rows in the same terminal transaction; the background
            // reconciler is a second line of repair, not a window in which a failed ticket works.
            foreach (var ticket in existing)
                ticket.Revoke(exception.Message, failedAt);
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
