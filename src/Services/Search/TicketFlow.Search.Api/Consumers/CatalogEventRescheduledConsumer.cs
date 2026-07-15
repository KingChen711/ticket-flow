using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Search.Application.Abstractions;

namespace TicketFlow.Search.Api.Consumers;

/// <summary>
/// Real integration event, first consumer ever (Commit 1 added the contract, Commit 2 wires this
/// — before this, catalog's Event.Reschedule() domain event went nowhere, see phase-6-plan.md
/// Commit 1). Partial update only — see IEventSearchIndex.UpdateStartsAtAsync's doc comment for
/// why this can't be a full re-index.
/// </summary>
public sealed class CatalogEventRescheduledConsumer(IEventSearchIndex searchIndex) : IConsumer<CatalogEventRescheduled>
{
    public Task Consume(ConsumeContext<CatalogEventRescheduled> context) =>
        searchIndex.UpdateStartsAtAsync(context.Message.CatalogEventId, context.Message.StartsAt, context.CancellationToken);
}
