using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Application.EventDetails;
using TicketFlow.Catalog.Application.Events;

namespace TicketFlow.Catalog.Api.Endpoints;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/events").WithTags("Events");

        group.MapPost("/", async (CreateEventRequest request, EventService service, CancellationToken ct) =>
        {
            var @event = await service.CreateAsync(request, ct);
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
        // graceful degradation on either. userId is optional/trusted (no auth-derived identity
        // anywhere in this project yet, same gap noted on PlaceOrderCommand); omitted means
        // Recommendation falls back to its own popular-events cold-start path.
        group.MapGet("/{id:guid}/detail", async (Guid id, Guid? userId, EventDetailAggregator aggregator, CancellationToken ct) =>
        {
            var detail = await aggregator.GetDetailAsync(id, userId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        // "Update" in Phase 1 only covers rescheduling — Event exposes no other mutator for
        // Title/Description/Artist (see Event.cs); editing those isn't modeled yet.
        group.MapPut("/{id:guid}", async (Guid id, RescheduleEventRequest request, EventService service, CancellationToken ct) =>
            await RunGuardedAsync(() => service.RescheduleAsync(id, request, ct))).RequireAuthorization("RequireOrganizer");

        group.MapPost("/{id:guid}/publish", async (Guid id, EventService service, CancellationToken ct) =>
            await RunGuardedAsync(() => service.PublishAsync(id, ct))).RequireAuthorization("RequireOrganizer");

        group.MapPost("/{id:guid}/cancel", async (Guid id, EventService service, CancellationToken ct) =>
            await RunGuardedAsync(() => service.CancelAsync(id, ct))).RequireAuthorization("RequireOrganizer");

        group.MapPost("/{id:guid}/price-tiers", async (Guid id, AddPriceTierRequest request, EventService service, CancellationToken ct) =>
            await RunGuardedAsync(() => service.AddPriceTierAsync(id, request, ct))).RequireAuthorization("RequireOrganizer");
    }

    /// <summary>
    /// Commit 11: a concurrent mutation of the same Event loses the optimistic-concurrency race
    /// (EF's <c>xmin</c> token) and must not be silently retried against stale in-memory state —
    /// the loser's decision may already be wrong given what the winner just did. 409 tells the
    /// caller to reload the event and re-decide, not to blindly resend the same command.
    /// </summary>
    private static async Task<IResult> RunGuardedAsync(Func<Task<EventResponse>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (ConcurrencyConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }
}
