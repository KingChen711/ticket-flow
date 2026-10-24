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

        // Service-to-service lookup used by ordering to price an order (POST because the id list
        // can be long).
        app.MapPost("/events/{eventId:guid}/seat-sections", async (
            Guid eventId,
            SeatSectionsRequest request,
            ReservationService service,
            CancellationToken ct) =>
            Results.Ok(await service.GetSeatSectionsAsync(eventId, request.SeatIds, ct))
        ).WithTags("Availability");
    }
}
