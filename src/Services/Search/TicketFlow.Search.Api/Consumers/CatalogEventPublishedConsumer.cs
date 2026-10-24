using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Search.Application.Abstractions;
using TicketFlow.Search.Application.Events;

namespace TicketFlow.Search.Api.Consumers;

/// <summary>
/// Real indexing (Commit 2) — replaces the Phase 3 log-only stub. Revision-guarded since Commit 11
/// (see IEventSearchIndex's doc comment): a redelivered or out-of-order CatalogEventPublished can
/// no longer resurrect an event this index already knows was cancelled.
/// </summary>
public sealed class CatalogEventPublishedConsumer(IEventSearchIndex searchIndex) : IConsumer<CatalogEventPublished>
{
    public Task Consume(ConsumeContext<CatalogEventPublished> context)
    {
        ContractVersionMetrics.RecordInbound(nameof(CatalogEventPublished), context.Message.AggregateVersion);
        var message = context.Message;
        return searchIndex.ApplyPublishedAsync(
            new SearchDocument(
                message.CatalogEventId, message.Title, message.Artist, message.VenueName, message.City,
                message.StartsAt, "Published", message.AggregateVersion,
                TimeZoneId: message.TimeZoneId ?? "UTC"),
            context.CancellationToken);
    }
}
