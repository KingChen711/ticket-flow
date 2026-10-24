using TicketFlow.Reservation.Application.Seats;

namespace TicketFlow.Reservation.Api.Endpoints;

public static class AvailabilityEndpoints
{
    public static void MapAvailabilityEndpoints(this WebApplication app)
    {
        app.MapGet("/events/{eventId:guid}/availability", async (
            Guid eventId,
            ReservationService service,
            CancellationToken ct) =>
            Results.Ok(await service.GetAvailabilityAsync(eventId, ct))
        ).WithTags("Availability");
    }
}
