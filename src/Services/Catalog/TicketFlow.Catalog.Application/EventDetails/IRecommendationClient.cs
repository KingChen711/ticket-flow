namespace TicketFlow.Catalog.Application.EventDetails;

/// <summary>
/// Application knows it needs "recommendations for this user", not HOW (HTTP call to
/// Recommendation's REST endpoint in Infrastructure). Contract: never throws for
/// timeout/connection/non-2xx failures — returns an empty list instead, same as
/// ISeatAvailabilityClient.
/// </summary>
public interface IRecommendationClient
{
    Task<IReadOnlyList<RecommendedEventSummary>> GetRecommendationsAsync(Guid userId, CancellationToken cancellationToken = default);
}
