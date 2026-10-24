namespace TicketFlow.Contracts.Messaging.Catalog;

/// <summary>
/// Integration event (Phase 6): catalog's Event was cancelled. Same "read model must hear about
/// every change, not just creation" reasoning as <see cref="CatalogEventRescheduled"/> — search
/// removes the cancelled event from its index on receipt.
/// </summary>
public sealed record CatalogEventCancelled(
    Guid CatalogEventId,
    long AggregateVersion = 0,
    DateTimeOffset? OccurredAt = null);
