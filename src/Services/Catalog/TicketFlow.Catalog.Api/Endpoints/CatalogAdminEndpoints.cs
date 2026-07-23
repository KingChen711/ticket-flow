using TicketFlow.Catalog.Application.Events;

namespace TicketFlow.Catalog.Api.Endpoints;

/// <summary>Operator recovery tool (Commit 11) — see <see cref="EventService.ReplayAsync"/>.</summary>
public static class CatalogAdminEndpoints
{
    public static void MapCatalogAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/events/replay", async (EventService service, CancellationToken ct) =>
            Results.Ok(new { replayed = await service.ReplayAsync(ct) }))
            .WithTags("Admin")
            .RequireAuthorization("RequireAdmin");
    }
}
