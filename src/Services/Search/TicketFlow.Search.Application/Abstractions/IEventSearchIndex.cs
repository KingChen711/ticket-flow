using TicketFlow.Search.Application.Events;

namespace TicketFlow.Search.Application.Abstractions;

/// <summary>
/// Application knows it needs "index/patch/remove/search this event", not HOW (Elasticsearch in
/// Infrastructure). Separate UpdateStartsAtAsync from UpsertAsync on purpose: the reschedule
/// consumer only ever receives CatalogEventId/StartsAt/EndsAt (Contracts.Messaging.Catalog.
/// CatalogEventRescheduled) — it has no Title/Artist/VenueName/City to do a full re-index with,
/// so it needs a genuine partial-update operation, not UpsertAsync called with placeholder data.
/// </summary>
public interface IEventSearchIndex
{
    /// <summary>Full upsert by EventId — used on CatalogEventPublished (first index or re-publish, idempotent).</summary>
    Task UpsertAsync(SearchDocument document, CancellationToken cancellationToken = default);

    /// <summary>Partial update — used on CatalogEventRescheduled, which doesn't carry the rest of the document.</summary>
    Task UpdateStartsAtAsync(Guid eventId, DateTimeOffset startsAt, CancellationToken cancellationToken = default);

    /// <summary>Used on CatalogEventCancelled — cancelled events are removed from search results entirely (not just flagged).</summary>
    Task DeleteAsync(Guid eventId, CancellationToken cancellationToken = default);

    Task<SearchEventsResult> SearchAsync(SearchEventsQuery query, CancellationToken cancellationToken = default);
}
