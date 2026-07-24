using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Events;
using TicketFlow.Catalog.Domain.Workflows;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Contracts.Messaging.Workflows;

namespace TicketFlow.Catalog.Application.Workflows;

public sealed class EventCancellationWorkflowService(
    IEventCancellationWorkflowRepository workflows,
    IEventRepository events,
    IIntegrationEventPublisher publisher,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<EventCancellationWorkflowResponse> StartAsync(
        Guid eventId,
        string? reason,
        CatalogCaller caller,
        CancellationToken cancellationToken = default)
    {
        // Ownership is checked before the already-completed shortcut below: replying "already
        // done" to an organizer who never owned this event still tells them it exists and was
        // cancelled, and cancelling is the single most destructive thing this API does — it closes
        // sale and voids every unconfirmed hold (Commit 3).
        var @event = await events.GetByIdAsync(eventId, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{eventId}' was not found.");
        if (!caller.IsAdmin && !@event.IsOwnedBy(caller.OrganizerId))
            throw new EventOwnershipException(eventId);

        var existing = await workflows.GetByEventIdAsync(eventId, cancellationToken);
        if (existing?.Status == EventCancellationStatus.Completed)
            return Map(existing);
        var now = timeProvider.GetUtcNow();
        var workflow = existing ?? EventCancellationWorkflow.Start(
            eventId,
            reason ?? "Event cancelled by organizer.",
            now);
        if (existing is null)
            workflows.Add(workflow);

        // Closing the sale and publishing the campaign hand-off share Catalog's EF outbox
        // transaction. Ordering cannot see the campaign before Reservation can see the closed
        // sale policy.
        if (@event.Status != EventStatus.Cancelled)
        {
            @event.Cancel();
            await publisher.PublishAsync(
                new CatalogEventCancelled(@event.Id, @event.AggregateVersion, now),
                cancellationToken);
        }

        // Always replay the closed policy with this workflow id, including when adopting a
        // legacy already-cancelled event or retrying a Requested workflow. Reservation emits
        // EventCancellationRequested only after this snapshot is durable and every active hold
        // has been invalidated, which also wakes a campaign after any crash window.
        await publisher.PublishAsync(
            new CatalogSalePolicyChanged(
                @event.Id,
                @event.Status.ToString(),
                now,
                @event.Schedule.StartsAt,
                @event.PriceTiers
                    .Select(tier => new PriceTierSnapshotV2(
                        tier.SectionId, tier.Price.Amount, tier.Price.Currency))
                    .ToList(),
                @event.PriceSnapshotVersion,
                now,
                workflow.Id,
                workflow.Reason),
            cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // A concurrent cancel for this event either wins the unique EventId insert or the
            // Event xmin update. Join its durable workflow; only rethrow when the conflict was an
            // unrelated mutation and no cancellation workflow exists.
            unitOfWork.DiscardChanges();
            var winner = await workflows.GetByEventIdAsync(eventId, cancellationToken);
            if (winner is null)
                throw;
            workflow = winner;
        }

        return Map(workflow);
    }

    public async Task<EventCancellationWorkflowResponse?> GetAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var workflow = await workflows.GetByIdAsync(workflowId, cancellationToken);
        return workflow is null ? null : Map(workflow);
    }

    public static EventCancellationWorkflowResponse Map(EventCancellationWorkflow workflow) => new(
        workflow.Id,
        workflow.EventId,
        workflow.Status.ToString(),
        workflow.Reason,
        workflow.OrderCount,
        workflow.RequestedAt,
        workflow.CompletedAt);
}
