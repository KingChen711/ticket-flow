using TicketFlow.Recommendation.Application.Recommendations;

namespace TicketFlow.Recommendation.Api.Endpoints;

public static class RecommendationEndpoints
{
    public static void MapRecommendationEndpoints(this WebApplication app)
    {
        // Empty-safe by construction — GetRecommendationsQueryHandler always falls back to
        // popular events rather than erroring, so a user with no history / no matches still
        // gets 200 with a (possibly empty, if nothing has ever been purchased) list.
        app.MapGet("/recommendations", async (
            Guid userId,
            GetRecommendationsQueryHandler handler,
            CancellationToken ct) => Results.Ok(await handler.HandleAsync(userId, ct)))
            .WithTags("Recommendations");
    }
}
