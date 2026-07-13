namespace TicketFlow.Contracts.Messaging.Catalog;

/// <summary>
/// Integration event: catalog's Event aggregate transitioned Draft -> Published. This is a
/// stable cross-service contract, NOT the catalog domain event of the same short name
/// (TicketFlow.Catalog.Domain.Events.EventPublished, which only carries the aggregate id and is
/// consumed in-process). Consumers (notification, search) depend on this shape; changing it is a
/// cross-service breaking change, unlike changing the domain event.
///
/// Carries enough data for consumers to act without calling back into catalog — no aggregate
/// references, no internal ids beyond what's needed to identify/display the event.
/// </summary>
public sealed record CatalogEventPublished(
    Guid CatalogEventId,
    string Title,
    Guid VenueId,
    DateTimeOffset StartsAt,
    DateTimeOffset PublishedAt);
