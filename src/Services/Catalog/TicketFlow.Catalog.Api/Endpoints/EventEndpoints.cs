using System.Security.Claims;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Application.EventDetails;
using TicketFlow.Catalog.Application.Events;
using TicketFlow.Catalog.Application.Workflows;

namespace TicketFlow.Catalog.Api.Endpoints;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/events").WithTags("Events");

        group.MapPost("/", async (CreateEventRequest request, ClaimsPrincipal user, EventService service, CancellationToken ct) =>
        {
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

            var @event = await service.CreateAsync(request, caller, ct);
            return Results.Created($"/events/{@event.Id}", @event);
        }).RequireAuthorization("RequireOrganizer");

        group.MapGet("/", async (EventService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, EventService service, CancellationToken ct) =>
        {
            var @event = await service.GetByIdAsync(id, ct);
            return @event is null ? Results.NotFound() : Results.Ok(@event);
        });

        // Ch5 aggregator (Commit 5) — fan-out to reservation (seats) + recommendation (gợi ý),
        // graceful degradation on either. Commit 14: the personalization identity is the caller's
        // own verified `sub`, never a userId the caller supplies. This route is anonymous-friendly
        // (the event page is public), so an unauthenticated visitor simply gets the cold-start
        // popular path — which is the same thing they got before, minus the ability to ask for
        // somebody else's recommendations by putting a stranger's id in the query string.
        group.MapGet("/{id:guid}/detail", async (Guid id, ClaimsPrincipal user, EventDetailAggregator aggregator, CancellationToken ct) =>
        {
            var userId = Guid.TryParse(user.FindFirstValue("sub"), out var subject) ? subject : (Guid?)null;
            var detail = await aggregator.GetDetailAsync(id, userId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        // Gateway-owned media execution still asks Catalog, the owner of the Event aggregate, for
        // the resource decision. The caller's bearer token is validated independently here; the
        // gateway's RequireOrganizer policy is defense in depth, not a substituted trust boundary.
        group.MapGet("/{id:guid}/media-upload-authorization", async (
            Guid id,
            ClaimsPrincipal user,
            EventService service,
            CancellationToken ct) =>
        {
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

            var access = await service.CheckAccessAsync(id, caller, ct);
            return access switch
            {
                EventAccessResult.Allowed => Results.NoContent(),
                EventAccessResult.Forbidden => Results.Json(
                    new { error = "The caller does not own this event." },
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization("RequireOrganizer");

        // "Update" in Phase 1 only covers rescheduling — Event exposes no other mutator for
        // Title/Description/Artist (see Event.cs); editing those isn't modeled yet.
        group.MapPut("/{id:guid}", async (Guid id, RescheduleEventRequest request, ClaimsPrincipal user, EventService service, CancellationToken ct) =>
            await RunGuardedAsync(user, caller => service.RescheduleAsync(id, request, caller, ct))).RequireAuthorization("RequireOrganizer");

        group.MapPost("/{id:guid}/publish", async (Guid id, ClaimsPrincipal user, EventService service, CancellationToken ct) =>
            await RunGuardedAsync(user, caller => service.PublishAsync(id, caller, ct))).RequireAuthorization("RequireOrganizer");

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            HttpRequest request,
            ClaimsPrincipal user,
            EventCancellationWorkflowService service,
            CancellationToken ct) =>
        {
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

            try
            {
                var reason = request.Query["reason"].FirstOrDefault();
                var workflow = await service.StartAsync(id, reason, caller, ct);
                return Results.Accepted($"/catalog/workflows/event-cancellations/{workflow.WorkflowId}", workflow);
            }
            catch (EventOwnershipException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ConcurrencyConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        }).RequireAuthorization("RequireOrganizer");

        group.MapPost("/{id:guid}/price-tiers", async (Guid id, AddPriceTierRequest request, ClaimsPrincipal user, EventService service, CancellationToken ct) =>
            await RunGuardedAsync(user, caller => service.AddPriceTierAsync(id, request, caller, ct))).RequireAuthorization("RequireOrganizer");
    }

    /// <summary>
    /// Commit 11: a concurrent mutation of the same Event loses the optimistic-concurrency race
    /// (EF's <c>xmin</c> token) and must not be silently retried against stale in-memory state —
    /// the loser's decision may already be wrong given what the winner just did. 409 tells the
    /// caller to reload the event and re-decide, not to blindly resend the same command.
    /// Commit 14 adds the ownership outcome: 403, deliberately not 404 — see
    /// <see cref="EventOwnershipException"/> for why hiding the event here would conceal nothing.
    /// </summary>
    private static async Task<IResult> RunGuardedAsync(
        ClaimsPrincipal user,
        Func<CatalogCaller, Task<EventResponse>> action)
    {
        if (!CatalogCallerFactory.TryCreate(user, out var caller))
            return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

        try
        {
            return Results.Ok(await action(caller));
        }
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

/// <summary>
/// Builds a <see cref="CatalogCaller"/> from the verified token — the single place the API layer
/// decides who is asking, so no endpoint can accidentally read identity from a request body.
/// </summary>
public static class CatalogCallerFactory
{
    public static bool TryCreate(ClaimsPrincipal user, out CatalogCaller caller)
    {
        // Admin is resolved before the subject parse: an admin acting on a legacy event still needs
        // to get through even if their own subject claim were unusable.
        var isAdmin = user.IsInRole("admin");
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var organizerId))
        {
            caller = new CatalogCaller(Guid.Empty, isAdmin);
            return isAdmin;
        }

        caller = new CatalogCaller(organizerId, isAdmin);
        return true;
    }
}
