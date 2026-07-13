using TicketFlow.Reservation.Application.Seats;

namespace TicketFlow.Reservation.Api.Endpoints;

/// <summary>Body-only DTO — eventId comes from the route, not duplicated in the JSON body.</summary>
public sealed record ProvisionInventoryBody(IReadOnlyList<ProvisionSectionRequest> Sections);

public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        app.MapPost("/events/{eventId:guid}/inventory", async (
            Guid eventId,
            ProvisionInventoryBody body,
            InventoryService service,
            CancellationToken ct) =>
        {
            var seatCount = await service.ProvisionAsync(new ProvisionInventoryRequest(eventId, body.Sections), ct);
            return Results.Created($"/events/{eventId}/availability", new { eventId, seatCount });
        }).WithTags("Inventory");
    }
}
