using TicketFlow.Catalog.Application.Venues;

namespace TicketFlow.Catalog.Api.Endpoints;

public static class VenueEndpoints
{
    public static void MapVenueEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/venues").WithTags("Venues");

        group.MapPost("/", async (RegisterVenueRequest request, VenueService service, CancellationToken ct) =>
        {
            var venue = await service.RegisterAsync(request, ct);
            return Results.Created($"/venues/{venue.Id}", venue);
        }).RequireAuthorization("RequireOrganizer");

        group.MapGet("/", async (VenueService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, VenueService service, CancellationToken ct) =>
        {
            var venue = await service.GetByIdAsync(id, ct);
            return venue is null ? Results.NotFound() : Results.Ok(venue);
        });
    }
}
