using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Search.Application.Abstractions;
using TicketFlow.Search.Application.Events;

namespace TicketFlow.Search.Api.Consumers;

/// <summary>
/// Real indexing (Commit 2) — replaces the Phase 3 log-only stub. Idempotent by construction:
/// UpsertAsync always writes to the same ES document _id (EventId), so a redelivered or
/// re-published CatalogEventPublished overwrites in place rather than creating a duplicate.
/// </summary>
public sealed class CatalogEventPublishedConsumer(IEventSearchIndex searchIndex) : IConsumer<CatalogEventPublished>
{
    public Task Consume(ConsumeContext<CatalogEventPublished> context)
    {
        var message = context.Message;
        return searchIndex.UpsertAsync(
            new SearchDocument(
                message.CatalogEventId, message.Title, message.Artist, message.VenueName, message.City,
                message.StartsAt, "Published"),
            context.CancellationToken);
    }
}
