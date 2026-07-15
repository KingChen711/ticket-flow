using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TicketFlow.Catalog.Application.EventDetails;

namespace TicketFlow.Catalog.Infrastructure.EventDetails;

/// <summary>Same degrade-at-the-boundary reasoning as HttpSeatAvailabilityClient.</summary>
public sealed class HttpRecommendationClient(HttpClient httpClient, ILogger<HttpRecommendationClient> logger)
    : IRecommendationClient
{
    public async Task<IReadOnlyList<RecommendedEventSummary>> GetRecommendationsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var recommendations = await httpClient.GetFromJsonAsync<List<RecommendedEventDto>>($"/recommendations?userId={userId}", cancellationToken);
            return recommendations?
                .Select(r => new RecommendedEventSummary(r.EventId, r.Title, r.Artist, r.VenueName, r.City, r.StartsAt))
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recommendation unavailable for user {UserId} — degrading to empty recommendations.", userId);
            return [];
        }
    }

    private sealed record RecommendedEventDto(
        Guid EventId, string Title, string Artist, string VenueName, string City, DateTimeOffset StartsAt);
}
