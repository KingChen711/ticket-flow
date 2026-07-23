using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Application.SeatMaps;

namespace TicketFlow.Catalog.Api.Endpoints;

public static class SeatMapEndpoints
{
    public static void MapSeatMapEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/events/{eventId:guid}/seatmap").WithTags("SeatMaps");

        group.MapPost("/", async (Guid eventId, SeatMapService service, CancellationToken ct) =>
        {
            var seatMap = await service.CreateForEventAsync(eventId, ct);
            return Results.Created($"/events/{eventId}/seatmap", seatMap);
        }).RequireAuthorization("RequireOrganizer");

        group.MapGet("/", async (Guid eventId, SeatMapService service, CancellationToken ct) =>
        {
            var seatMap = await service.GetByEventIdAsync(eventId, ct);
            return seatMap is null ? Results.NotFound() : Results.Ok(seatMap);
        });

        // Commit 11: two concurrent AddSection calls for two different names can each miss the
        // other's not-yet-saved section — the unique index is the actual backstop, and a race it
        // catches must come back as 409, not 500.
        group.MapPost("/sections", async (Guid eventId, AddSectionRequest request, SeatMapService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.AddSectionAsync(eventId, request, ct));
            }
            catch (ConcurrencyConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        }).RequireAuthorization("RequireOrganizer");
    }
}
