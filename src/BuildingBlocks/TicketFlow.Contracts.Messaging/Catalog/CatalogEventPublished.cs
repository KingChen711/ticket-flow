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
/// <remarks>
/// <see cref="Artist"/>/<see cref="VenueName"/>/<see cref="City"/> added in Phase 6 Commit 1 —
/// search needs to filter/index by artist and venue/city (scope mục 4: "tìm sự kiện theo nghệ
/// sĩ/venue/ngày/thành phố"), and the alternative (search calling back into catalog per event to
/// fill in the gap) would reintroduce the exact synchronous coupling this event's own doc comment
/// already rejects. Catalog already has both Event.Artist and the Venue aggregate on hand at
/// publish time — denormalizing here is the correct pub/sub idiom, not a shortcut. Backward
/// compatible: existing consumers (notification) that don't read these fields are unaffected.
/// </remarks>
public sealed record CatalogEventPublished(
    Guid CatalogEventId,
    string Title,
    Guid VenueId,
    DateTimeOffset StartsAt,
    DateTimeOffset PublishedAt,
    string Artist,
    string VenueName,
    string City);
