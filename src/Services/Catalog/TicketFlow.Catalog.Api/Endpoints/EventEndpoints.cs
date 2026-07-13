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
        });

        group.MapGet("/", async (EventService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, EventService service, CancellationToken ct) =>
        {
            var @event = await service.GetByIdAsync(id, ct);
            return @event is null ? Results.NotFound() : Results.Ok(@event);
        });

        // "Update" in Phase 1 only covers rescheduling — Event exposes no other mutator for
        // Title/Description/Artist (see Event.cs); editing those isn't modeled yet.
        group.MapPut("/{id:guid}", async (Guid id, RescheduleEventRequest request, EventService service, CancellationToken ct) =>
            Results.Ok(await service.RescheduleAsync(id, request, ct)));

        group.MapPost("/{id:guid}/publish", async (Guid id, EventService service, CancellationToken ct) =>
            Results.Ok(await service.PublishAsync(id, ct)));

        group.MapPost("/{id:guid}/cancel", async (Guid id, EventService service, CancellationToken ct) =>
            Results.Ok(await service.CancelAsync(id, ct)));

        group.MapPost("/{id:guid}/price-tiers", async (Guid id, AddPriceTierRequest request, EventService service, CancellationToken ct) =>
            Results.Ok(await service.AddPriceTierAsync(id, request, ct)));
    }
}
