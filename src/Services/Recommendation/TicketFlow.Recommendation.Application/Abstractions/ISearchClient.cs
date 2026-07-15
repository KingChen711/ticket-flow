using TicketFlow.Recommendation.Application.Recommendations;

namespace TicketFlow.Recommendation.Application.Abstractions;

/// <summary>
/// Application knows it needs "event details/similar events", not HOW (an HTTP call to the
/// Search service in Infrastructure). Recommendation deliberately does not keep its own copy of
/// catalog/search data (Phase 6 mục 5) — it queries Search's already-indexed read model instead,
/// the same "nghiêng về hỏi ES" call made for Commit 4 in docs/phase-6-plan.md.
/// </summary>
public interface ISearchClient
{
    /// <summary>Null when Search has no document for this EventId (e.g. the event was since cancelled and removed from the index).</summary>
    Task<RecommendedEvent?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecommendedEvent>> FindByArtistAsync(string artist, CancellationToken cancellationToken = default);

    /// <summary>City-filtered — Search's endpoint has no dedicated VenueName filter (Commit 2's own
    /// documented decision); "same venue" is done by narrowing to City here and matching VenueName
    /// exactly in the caller, rather than reopening that contract.</summary>
    Task<IReadOnlyList<RecommendedEvent>> FindByCityAsync(string city, CancellationToken cancellationToken = default);
}
