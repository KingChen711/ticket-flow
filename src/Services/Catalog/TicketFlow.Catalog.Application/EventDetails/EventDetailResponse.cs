using TicketFlow.Catalog.Application.Events;
using TicketFlow.Catalog.Application.Venues;

namespace TicketFlow.Catalog.Application.EventDetails;

public sealed record SeatAvailabilityResponse(Guid SeatId, Guid SectionId, string Label, string Status);

public sealed record RecommendedEventSummary(
    Guid EventId, string Title, string Artist, string VenueName, string City, DateTimeOffset StartsAt);

/// <summary>
/// Event/Venue are the primary source (catalog, local) — always present, or the whole request
/// 404s. Seats/Recommendations are secondary sources (reservation/recommendation, over HTTP) —
/// best-effort: empty on failure/timeout, never the reason this endpoint 500s.
/// </summary>
public sealed record EventDetailResponse(
    EventResponse Event,
    VenueResponse Venue,
    IReadOnlyList<SeatAvailabilityResponse> Seats,
    IReadOnlyList<RecommendedEventSummary> Recommendations);
