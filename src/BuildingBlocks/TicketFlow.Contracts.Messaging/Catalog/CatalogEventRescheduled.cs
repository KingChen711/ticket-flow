namespace TicketFlow.Contracts.Messaging.Catalog;

/// <summary>
/// Integration event (Phase 6): catalog's Event was rescheduled to a new time. Published
/// alongside the existing in-process-only domain event TicketFlow.Catalog.Domain.Events.
/// EventRescheduled — that one is ignored by EF Core (Ignore(DomainEvents)) and goes nowhere;
/// this is the actual cross-service contract search consumes to keep its index in sync. A read
/// model that only ever hears about an event's CREATION and never its later changes goes stale —
/// this is exactly the gap search would otherwise have (see phase-6-plan.md architecture
/// decision 2).
/// </summary>
public sealed record CatalogEventRescheduled(
    Guid CatalogEventId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    long AggregateVersion = 0,
    DateTimeOffset? OccurredAt = null,
    string? TimeZoneId = null);
