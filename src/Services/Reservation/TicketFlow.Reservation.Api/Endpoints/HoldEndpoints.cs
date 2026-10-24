using System.Security.Claims;
using TicketFlow.Reservation.Application.Seats;

namespace TicketFlow.Reservation.Api.Endpoints;

public sealed record CreateHoldRequest(Guid EventId, IReadOnlyList<Guid> SeatIds);

public static class HoldEndpoints
{
    public static void MapHoldEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/holds").WithTags("Holds").RequireAuthorization("RequireAuthenticated");

        group.MapPost("/", async (
            CreateHoldRequest request,
            ClaimsPrincipal user,
            ReservationService service,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var ownerUserId))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

            var result = await service.HoldAsync(
                new HoldSeatsRequest(request.EventId, request.SeatIds, ownerUserId), ct);

            // A throttled caller must be able to tell "you are asking for too much" apart from
            // "these seats are gone" — otherwise a per-user cap is indistinguishable from a real
            // sold-out event for both the UI and anyone probing availability (Commit 5 delta).
            return result.Reason switch
            {
                HoldRejectionReason.RateLimited => Results.Json(result, statusCode: StatusCodes.Status429TooManyRequests),
                HoldRejectionReason.HoldCapExceeded => Results.Json(result, statusCode: StatusCodes.Status409Conflict),
                HoldRejectionReason.SeatsUnavailable => Results.Conflict(result),
                _ => Results.Ok(result),
            };
        });

        group.MapGet("/{holdId:guid}/quote", async (
            Guid holdId,
            ClaimsPrincipal user,
            ReservationService service,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var ownerUserId))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

            var quote = await service.GetQuoteAsync(holdId, ownerUserId, ct);
            return quote is null ? Results.NotFound() : Results.Ok(quote);
        });

        group.MapPost("/{holdId:guid}/release", async (
            Guid holdId,
            ClaimsPrincipal user,
            ReservationService service,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirstValue("sub"), out var ownerUserId))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

            var released = await service.ReleaseOwnedHoldAsync(holdId, ownerUserId, ct);
            return released ? Results.NoContent() : Results.NotFound();
        });
    }
}
