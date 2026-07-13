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
        });

        group.MapGet("/", async (Guid eventId, SeatMapService service, CancellationToken ct) =>
        {
            var seatMap = await service.GetByEventIdAsync(eventId, ct);
            return seatMap is null ? Results.NotFound() : Results.Ok(seatMap);
        });

        group.MapPost("/sections", async (Guid eventId, AddSectionRequest request, SeatMapService service, CancellationToken ct) =>
            Results.Ok(await service.AddSectionAsync(eventId, request, ct)));
    }
}
