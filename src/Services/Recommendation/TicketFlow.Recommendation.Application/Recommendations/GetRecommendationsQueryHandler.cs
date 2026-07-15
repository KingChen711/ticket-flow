using TicketFlow.Recommendation.Application.Abstractions;

namespace TicketFlow.Recommendation.Application.Recommendations;

/// <summary>
/// No ML on purpose (Phase 6 mục 5 — the point of this commit is "service + Mongo read model +
/// event-driven", not a recommendation algorithm). Two strategies, in order: same-artist/same-venue
/// as something the user already purchased; popular (most distinct purchasers) as the cold-start
/// fallback when the user has no history, or when their history yields nothing (e.g. the only
/// artist/venue matches are things already purchased).
/// </summary>
public sealed class GetRecommendationsQueryHandler(IPurchaseHistoryStore historyStore, ISearchClient searchClient)
{
    private const int PopularTake = 10;
    private const int MaxRecommendations = 10;

    public async Task<IReadOnlyList<RecommendedEvent>> HandleAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var history = await historyStore.GetAsync(userId, cancellationToken);
        if (history is null || history.PurchasedEventIds.Count == 0)
            return await GetPopularAsync(cancellationToken);

        var seen = new HashSet<Guid>(history.PurchasedEventIds);
        var results = new List<RecommendedEvent>();

        foreach (var purchasedEventId in history.PurchasedEventIds)
        {
            if (results.Count >= MaxRecommendations)
                break;

            var purchased = await searchClient.GetByIdAsync(purchasedEventId, cancellationToken);
            if (purchased is null)
                continue;

            foreach (var candidate in await searchClient.FindByArtistAsync(purchased.Artist, cancellationToken))
                TryAdd(candidate, seen, results);

            foreach (var candidate in await searchClient.FindByCityAsync(purchased.City, cancellationToken))
                if (candidate.VenueName == purchased.VenueName)
                    TryAdd(candidate, seen, results);
        }

        return results.Count > 0 ? results : await GetPopularAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<RecommendedEvent>> GetPopularAsync(CancellationToken cancellationToken)
    {
        var popularEventIds = await historyStore.GetPopularEventIdsAsync(PopularTake, cancellationToken);

        var results = new List<RecommendedEvent>();
        foreach (var eventId in popularEventIds)
        {
            var recommendedEvent = await searchClient.GetByIdAsync(eventId, cancellationToken);
            if (recommendedEvent is not null)
                results.Add(recommendedEvent);
        }

        return results;
    }

    private static void TryAdd(RecommendedEvent candidate, HashSet<Guid> seen, List<RecommendedEvent> results)
    {
        if (results.Count < MaxRecommendations && seen.Add(candidate.EventId))
            results.Add(candidate);
    }
}
