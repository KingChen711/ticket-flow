using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Workflows;
using TicketFlow.Messaging;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Api.Consumers;

/// <summary>
/// Persists a per-order cancellation tombstone before acknowledging revocation. The issuance
/// consumer reads the same unique job row, so a late IssueTickets either loses the insert race or
/// its xmin update and can never commit a valid ticket after this operation wins.
/// </summary>
public sealed class RevokeTicketsConsumer(
    ITicketRepository repository,
    ITicketIssuanceJobRepository jobs,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<RevokeTicketsConsumer> logger) : IConsumer<OrderCancelledIntegrationEvent>
{
    public async Task Consume(ConsumeContext<OrderCancelledIntegrationEvent> context)
    {
        var message = context.Message;
        var now = timeProvider.GetUtcNow();
        var eventId = message.CatalogEventId ?? Guid.Empty;
        var job = await jobs.GetByOrderAsync(message.OrderId, context.CancellationToken);
        if (job is null)
        {
            job = TicketIssuanceJob.CancelBeforeIssue(
                message.OrderId,
                eventId,
                message.UserId,
                message.Reason,
                now);
            jobs.Add(job);
        }
        else
        {
            job.MarkCancelled(message.Reason, now);
        }

        var tickets = await repository.GetByOrderIdAsync(message.OrderId, context.CancellationToken);
        foreach (var ticket in tickets)
            ticket.Revoke(message.Reason, now);

        if (message.CancellationWorkflowId is { } workflowId)
        {
            if (eventId == Guid.Empty)
                throw new PermanentMessageFaultException(
                    $"Event cancellation '{workflowId}' for order '{message.OrderId}' has no CatalogEventId.");

            await context.Publish(new TicketsRevokedForEventCancellation(
                workflowId,
                eventId,
                message.OrderId,
                tickets.Count,
                now));
        }

        try
        {
            // Ticket/job changes and the acknowledgement share Ticketing's EF bus-outbox commit.
            await unitOfWork.SaveChangesAsync(context.CancellationToken);
        }
        catch (Exception exception) when (jobs.IsUniqueViolation(exception))
        {
            throw new OutOfOrderMessageException(
                $"Ticket issuance and cancellation raced for order '{message.OrderId}'; retry against the durable winner.");
        }

        logger.LogInformation(
            "[ticketing] fenced issuance and revoked {Count} ticket(s) for cancelled order {OrderId}: {Reason}",
            tickets.Count,
            message.OrderId,
            message.Reason);
    }
}
