namespace TicketFlow.Search.Application.Events;

/// <summary>
/// The read model itself — deliberately a plain record, not an Entity/AggregateRoot. Search has
/// no Domain project (Phase 6 mục 8): this document has no invariants to guard, no lifecycle
/// beyond "upsert on publish, patch on reschedule, delete on cancel" — it's a pure projection of
/// catalog's CatalogEventPublished/Rescheduled/Cancelled integration events, same spirit as
/// MyOrderView (Phase 4) being a plain record rather than a guarded aggregate.
/// </summary>
/// <param name="Revision">
/// Catalog's <c>Event.AggregateVersion</c> at the time the source message was raised (Commit 11).
/// The document only ever moves to a message carrying a Revision at least as high as its own — the
/// property that lets three lifecycle messages for one event converge to the same final state no
/// matter what order RabbitMQ happens to deliver them in.
/// </param>
/// <param name="Tombstoned">
/// Set once and never cleared. Catalog's <c>Event.Cancel()</c> is a one-way domain transition — a
/// cancelled event can never legally be re-published or rescheduled — so once this read model has
/// seen a Cancel for an EventId, no later Published/Rescheduled for that id is real; it is a
/// stale, out-of-order message, whatever revision it happens to carry.
/// </param>
public sealed record SearchDocument(
    Guid EventId,
    string Title,
    string Artist,
    string VenueName,
    string City,
    DateTimeOffset StartsAt,
    string Status,
    long Revision = 0,
    bool Tombstoned = false,
    string TimeZoneId = "UTC");
