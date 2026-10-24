using System.Security.Claims;
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
            ClaimsPrincipal user,
            ISalesStore salesStore,
            CancellationToken ct) =>
        {
            if (currency is not null && (currency.Length != 3 || currency.Any(character => !char.IsLetter(character))))
                return Results.BadRequest(new { error = "Currency must be a three-letter ISO code." });

            // Commit 14: RequireOrganizer only ever meant "is an organizer", so every organizer
            // could read the whole platform's revenue — including a competitor's per-event takings
            // and seat counts — simply by omitting eventId. The scope is the caller's own `sub`,
            // and passing eventId explicitly does not widen it: an event the caller does not own
            // now intersects to nothing rather than answering.
            Guid? organizerScope = null;
            if (!user.IsInRole("admin"))
            {
                if (!Guid.TryParse(user.FindFirstValue("sub"), out var organizerId))
                    return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

                organizerScope = organizerId;
            }

            var result = await salesStore.GetRevenueAsync(
                new RevenueQuery(eventId, from, to, currency, organizerScope), ct);
            return Results.Ok(result);
        }).WithTags("Analytics").RequireAuthorization("RequireOrganizer");
    }
}
