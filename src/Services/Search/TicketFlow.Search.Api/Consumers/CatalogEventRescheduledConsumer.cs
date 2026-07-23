using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Search.Application.Abstractions;

namespace TicketFlow.Search.Api.Consumers;

/// <summary>
/// Real integration event, first consumer ever (Commit 1 added the contract, Commit 2 wires this
/// — before this, catalog's Event.Reschedule() domain event went nowhere, see phase-6-plan.md
/// Commit 1). Partial update only — this message carries nothing else. Revision-guarded and
/// upserts a placeholder if the document doesn't exist yet (Commit 11) — before this, a Reschedule
/// that overtook Published found no document to patch and silently lost the new StartsAt forever.
/// </summary>
public sealed class CatalogEventRescheduledConsumer(IEventSearchIndex searchIndex) : IConsumer<CatalogEventRescheduled>
{
    public Task Consume(ConsumeContext<CatalogEventRescheduled> context)
    {
        ContractVersionMetrics.RecordInbound(nameof(CatalogEventRescheduled), context.Message.AggregateVersion);
        return searchIndex.ApplyRescheduledAsync(
            context.Message.CatalogEventId,
            context.Message.StartsAt,
            context.Message.AggregateVersion,
            context.CancellationToken);
    }
}
