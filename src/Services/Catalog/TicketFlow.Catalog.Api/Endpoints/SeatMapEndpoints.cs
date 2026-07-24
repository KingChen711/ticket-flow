using System.Security.Claims;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Application.SeatMaps;

namespace TicketFlow.Catalog.Api.Endpoints;

public static class SeatMapEndpoints
{
    public static void MapSeatMapEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/events/{eventId:guid}/seatmap").WithTags("SeatMaps");

        group.MapPost("/", async (Guid eventId, ClaimsPrincipal user, SeatMapService service, CancellationToken ct) =>
            await RunGuardedAsync(user, caller => service.CreateForEventAsync(eventId, caller, ct)))
            .RequireAuthorization("RequireOrganizer");

        group.MapGet("/", async (Guid eventId, SeatMapService service, CancellationToken ct) =>
        {
            var seatMap = await service.GetByEventIdAsync(eventId, ct);
            return seatMap is null ? Results.NotFound() : Results.Ok(seatMap);
        });

        // Commit 11: two concurrent AddSection calls for two different names can each miss the
        // other's not-yet-saved section — the unique index is the actual backstop, and a race it
        // catches must come back as 409, not 500.
        group.MapPost("/sections", async (Guid eventId, AddSectionRequest request, ClaimsPrincipal user, SeatMapService service, CancellationToken ct) =>
            await RunGuardedAsync(user, caller => service.AddSectionAsync(eventId, request, caller, ct)))
            .RequireAuthorization("RequireOrganizer");
    }

    private static async Task<IResult> RunGuardedAsync(
        ClaimsPrincipal user,
        Func<CatalogCaller, Task<SeatMapResponse>> action)
    {
        if (!CatalogCallerFactory.TryCreate(user, out var caller))
            return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

        try
        {
            return Results.Ok(await action(caller));
        }
        // Commit 14: a seat map belongs to whoever owns its event — see SeatMapService.EnsureOwnedAsync.
        catch (EventOwnershipException exception)
        {
            return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ConcurrencyConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }
}
