namespace TicketFlow.Search.Application.Events;

/// <summary>
/// The read model itself — deliberately a plain record, not an Entity/AggregateRoot. Search has
/// no Domain project (Phase 6 mục 8): this document has no invariants to guard, no lifecycle
/// beyond "upsert on publish, patch on reschedule, delete on cancel" — it's a pure projection of
/// catalog's CatalogEventPublished/Rescheduled/Cancelled integration events, same spirit as
/// MyOrderView (Phase 4) being a plain record rather than a guarded aggregate.
/// </summary>
public sealed record SearchDocument(
    Guid EventId,
    string Title,
    string Artist,
    string VenueName,
    string City,
    DateTimeOffset StartsAt,
    string Status);
