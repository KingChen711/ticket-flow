using System.Security.Claims;
using TicketFlow.Reservation.Application.Seats;

namespace TicketFlow.Reservation.Api.Endpoints;

/// <summary>
/// Confirm/release only need EventId in the body — the Redis key scheme (hold:{eventId}:{seatId})
/// requires it to locate the right keys, and the client already has it in context (it's the
/// same event it just held seats for). See docs/phase-2-plan.md for why holdId alone isn't enough.
/// </summary>
public sealed record HoldActionRequest(Guid EventId);
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
            return result.Success ? Results.Ok(result) : Results.Conflict(result);
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

        group.MapPost("/{holdId:guid}/confirm", async (
            Guid holdId,
            HoldActionRequest request,
            ReservationService service,
            CancellationToken ct) =>
        {
            var confirmed = await service.ConfirmAsync(request.EventId, holdId, ct);
            return confirmed.Count > 0 ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/{holdId:guid}/release", async (
            Guid holdId,
            HoldActionRequest request,
            ReservationService service,
            CancellationToken ct) =>
        {
            var released = await service.ReleaseAsync(request.EventId, holdId, ct);
            return released ? Results.NoContent() : Results.NotFound();
        });
    }
}
