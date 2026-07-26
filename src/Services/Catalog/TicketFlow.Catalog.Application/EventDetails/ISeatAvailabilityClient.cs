namespace TicketFlow.Catalog.Application.EventDetails;

/// <summary>
/// Application knows it needs "seat availability for this event", not HOW (HTTP call to
/// Reservation's REST availability endpoint in Infrastructure). Deliberately the REST
/// GET /events/{id}/availability, not the gRPC CheckSeatAvailability from Phase 1/2 — that RPC
/// requires a specific seat_ids list upfront (built for Ordering's pre-purchase check), whereas
/// the detail page needs the full seat list with per-seat status, which only the REST endpoint
/// already returns as-is. See docs/phase-6-plan.md Commit 5 for the full reasoning.
/// Contract: never throws for timeout/connection/non-2xx failures. It returns an explicit
/// unavailable result so the UI cannot confuse a dependency outage with a real zero-seat event.
/// </summary>
public interface ISeatAvailabilityClient
{
    Task<SeatAvailabilityResult> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default);
}

public sealed record SeatAvailabilityResult(
    IReadOnlyList<SeatAvailabilityResponse> Seats,
    bool IsAvailable)
{
    public static SeatAvailabilityResult Available(IReadOnlyList<SeatAvailabilityResponse> seats) => new(seats, true);
    public static SeatAvailabilityResult TemporarilyUnavailable() => new([], false);
}
