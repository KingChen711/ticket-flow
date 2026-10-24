using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Infrastructure;

namespace TicketFlow.Reservation.Api.Endpoints;

/// <summary>Body-only DTO — eventId comes from the route, not duplicated in the JSON body.</summary>
public sealed record ProvisionInventoryBody(IReadOnlyList<ProvisionSectionRequest> Sections);

/// <remarks>
/// Commit 14: both routes now require a service principal. They had no authorization at all —
/// anyone who could reach the port could provision or wipe an event's entire seat inventory, and
/// DELETE on a live event is about as destructive as this system gets. They are not user-facing:
/// the only caller is Catalog's event-onboarding workflow (HttpReservationInventoryClient), which
/// now presents a client-credentials token. RequireServicePrincipal rather than RequireOrganizer
/// on purpose — Reservation has no way to know which organizer owns an event, so "an organizer"
/// would be no narrower than "anyone logged in", and Catalog has already checked ownership before
/// it calls (EventOnboardingWorkflowService).
/// </remarks>
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
        }).WithTags("Inventory").RequireAuthorization("RequireServicePrincipal");

        app.MapDelete("/events/{eventId:guid}/inventory", async (
            Guid eventId,
            InventoryCleanupService cleanup,
            CancellationToken ct) =>
        {
            await cleanup.CleanupAsync(eventId, ct);
            return Results.NoContent();
        }).WithTags("Inventory").RequireAuthorization("RequireServicePrincipal");
    }
}
