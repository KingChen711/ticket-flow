using TicketFlow.Analytics.Application.Abstractions;
using TicketFlow.Analytics.Application.Sales;

namespace TicketFlow.Analytics.Api.Endpoints;

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        // Bare resource path, no "/analytics" prefix baked in — same convention as every other
        // service's own endpoints (Search's /events, Ordering's /orders, ...): the gateway owns
        // the service-name prefix (Commit 8), backend endpoints stay prefix-free.
        app.MapGet("/revenue", async (
            Guid? eventId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? currency,
            ISalesStore salesStore,
            CancellationToken ct) =>
        {
            if (currency is not null && (currency.Length != 3 || currency.Any(character => !char.IsLetter(character))))
                return Results.BadRequest(new { error = "Currency must be a three-letter ISO code." });

            var result = await salesStore.GetRevenueAsync(new RevenueQuery(eventId, from, to, currency), ct);
            return Results.Ok(result);
        }).WithTags("Analytics").RequireAuthorization("RequireOrganizer");
    }
}
