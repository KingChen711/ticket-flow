using TicketFlow.Catalog.Application.Events;
using TicketFlow.Catalog.Application.Venues;

namespace TicketFlow.Catalog.Application.EventDetails;

/// <summary>
/// Ch5 fan-out: Event+Venue (catalog, local, primary — 404s the whole request if missing) reads
/// concurrently with the two secondary HTTP sources (seats, recommendations). No try/catch here —
/// ISeatAvailabilityClient/IRecommendationClient are documented to never throw for
/// timeout/connection/non-2xx failures, only return an empty list; that's where the graceful
/// degradation actually lives (Infrastructure, next to the HttpClient that can fail), not here.
/// </summary>
public sealed class EventDetailAggregator(
    EventService eventService,
    VenueService venueService,
    ISeatAvailabilityClient seatAvailabilityClient,
    IRecommendationClient recommendationClient)
{
    public async Task<EventDetailResponse?> GetDetailAsync(Guid eventId, Guid? userId, CancellationToken cancellationToken = default)
    {
        // Started immediately, before the (local, sequential-by-nature) Event->Venue lookup —
        // by the time we need their results below, both are likely already done.
        var seatsTask = seatAvailabilityClient.GetAvailabilityAsync(eventId, cancellationToken);
        var recommendationsTask = recommendationClient.GetRecommendationsAsync(userId ?? Guid.Empty, cancellationToken);

        var @event = await eventService.GetByIdAsync(eventId, cancellationToken);
        if (@event is null)
            return null;

        var venue = await venueService.GetByIdAsync(@event.VenueId, cancellationToken)
            ?? throw new InvalidOperationException($"Venue {@event.VenueId} referenced by event {eventId} not found.");

        await Task.WhenAll(seatsTask, recommendationsTask);

        return new EventDetailResponse(@event, venue, await seatsTask, await recommendationsTask);
    }
}
