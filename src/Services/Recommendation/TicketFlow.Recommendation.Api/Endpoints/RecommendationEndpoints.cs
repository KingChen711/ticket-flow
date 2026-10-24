using System.Security.Claims;
using TicketFlow.Recommendation.Application.Recommendations;

namespace TicketFlow.Recommendation.Api.Endpoints;

public static class RecommendationEndpoints
{
    public static void MapRecommendationEndpoints(this WebApplication app)
    {
        // Empty-safe by construction — GetRecommendationsQueryHandler always falls back to
        // popular events rather than erroring, so a user with no history / no matches still
        // gets 200 with a (possibly empty, if nothing has ever been purchased) list.
        //
        // Commit 14: the identity is the caller's own verified `sub`. This route used to take
        // `Guid userId` straight off the query string, with no authentication in the service at
        // all, so anyone who could reach it — and, through the gateway, any logged-in customer —
        // could read the recommendations derived from another person's purchase history just by
        // putting their id in the URL. There is no longer a userId parameter to put anything in;
        // an anonymous caller gets the cold-start popular list, which is all an unidentified
        // caller was ever entitled to.
        app.MapGet("/recommendations", async (
            ClaimsPrincipal user,
            GetRecommendationsQueryHandler handler,
            CancellationToken ct) =>
        {
            var userId = Guid.TryParse(user.FindFirstValue("sub"), out var subject) ? subject : (Guid?)null;
            return Results.Ok(await handler.HandleAsync(userId, ct));
        }).WithTags("Recommendations");

        // The on-behalf-of path, for Catalog's event-detail aggregator. Same shape and the same
        // reasoning as Reservation's gRPC quote lookup (Commit 14): naming a user in the request
        // is only safe when the caller cannot BE that user, so this requires a service principal
        // and is not reachable through the gateway's public routes.
        app.MapGet("/internal/recommendations/{userId:guid}", async (
            Guid userId,
            GetRecommendationsQueryHandler handler,
            CancellationToken ct) => Results.Ok(await handler.HandleAsync(userId, ct)))
            .WithTags("Recommendations")
            .RequireAuthorization("RequireServicePrincipal");
    }
}
