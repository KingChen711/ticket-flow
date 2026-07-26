using TicketFlow.Catalog.Application.Events;
using TicketFlow.Catalog.Application.Venues;

namespace TicketFlow.Catalog.Application.EventDetails;

public sealed record SeatAvailabilityResponse(
    Guid SeatId,
    Guid SectionId,
    string Label,
    string Status,
    long Generation = 0,
    DateTimeOffset? UpdatedAt = null);

public sealed record RecommendedEventSummary(
    Guid EventId, string Title, string Artist, string VenueName, string City, DateTimeOffset StartsAt);

/// <summary>
/// Event/Venue are the primary source (catalog, local) — always present, or the whole request
/// 404s. Seats/Recommendations are secondary sources (reservation/recommendation, over HTTP) —
/// best-effort: an empty list can be genuine, while AvailabilityStatus explicitly distinguishes
/// dependency failure/timeout so callers never render an outage as sold out.
/// </summary>
public sealed record EventDetailResponse(
    EventResponse Event,
    VenueResponse Venue,
    IReadOnlyList<SeatAvailabilityResponse> Seats,
    IReadOnlyList<RecommendedEventSummary> Recommendations,
    string AvailabilityStatus);
