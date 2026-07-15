using TicketFlow.Analytics.Application.Abstractions;
using TicketFlow.Analytics.Application.Sales;

namespace TicketFlow.Analytics.Api.Endpoints;

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        app.MapGet("/analytics/revenue", async (
            Guid? eventId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            ISalesStore salesStore,
            CancellationToken ct) =>
        {
            var result = await salesStore.GetRevenueAsync(new RevenueQuery(eventId, from, to), ct);
            return Results.Ok(result);
        }).WithTags("Analytics");
    }
}
