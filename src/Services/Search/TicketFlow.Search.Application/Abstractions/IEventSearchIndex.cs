using TicketFlow.Search.Application.Events;

namespace TicketFlow.Search.Application.Abstractions;

/// <summary>
/// Application knows it needs "apply this lifecycle fact to the index, revision-guarded", not HOW
/// (Elasticsearch in Infrastructure).
/// </summary>
/// <remarks>
/// Commit 11 replaced the old shape (<c>UpsertAsync</c>/<c>UpdateStartsAtAsync</c>/<c>DeleteAsync</c>,
/// each an unconditional write) with three revision-guarded applies. The old shape had two real
/// gaps: nothing stopped an out-of-order <c>CatalogEventPublished</c> from overwriting a document
/// that had already been cancelled, and <c>UpdateStartsAtAsync</c>/<c>DeleteAsync</c> on a document
/// that had never been created (Rescheduled or Cancelled overtaking Published) silently did
/// nothing, dropping the fact entirely. Every method below both revision-guards its write AND
/// creates the document if it is missing, so all three lifecycle events converge to the same final
/// state regardless of delivery order.
/// </remarks>
public interface IEventSearchIndex
{
    /// <summary>
    /// Applies <c>CatalogEventPublished</c>: a full document write. Rejected if the stored document
    /// is tombstoned, or if <paramref name="document"/>'s revision is not at least the stored one.
    /// </summary>
    Task ApplyPublishedAsync(SearchDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <c>CatalogEventRescheduled</c>: a partial write (StartsAt only — the message carries
    /// nothing else). If no document exists yet, creates a placeholder with the fields this message
    /// actually has; a later Published fills in the rest, since Published's revision, being the
    /// earlier canonical mutation, is never higher than a reschedule that came after it.
    /// </summary>
    Task ApplyRescheduledAsync(
        Guid eventId,
        DateTimeOffset startsAt,
        long revision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <c>CatalogEventCancelled</c>. Unlike the other two, this one is never rejected: a
    /// tombstone always wins, because Catalog's domain forbids ever un-cancelling an event, so no
    /// future real message can legitimately contradict one already seen.
    /// </summary>
    Task ApplyCancelledAsync(Guid eventId, long revision, CancellationToken cancellationToken = default);

    /// <summary>Null when there is no document for this id, or it is tombstoned — a cancelled event is not "found".</summary>
    Task<SearchDocument?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>Never returns a tombstoned document — a cancelled event has no reason to appear in search results.</summary>
    Task<SearchEventsResult> SearchAsync(SearchEventsQuery query, CancellationToken cancellationToken = default);
}
