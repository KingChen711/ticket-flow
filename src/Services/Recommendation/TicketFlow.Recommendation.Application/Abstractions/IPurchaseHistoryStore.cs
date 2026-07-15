using TicketFlow.Recommendation.Application.Purchases;

namespace TicketFlow.Recommendation.Application.Abstractions;

/// <summary>
/// Application knows it needs "record this purchase for this user", not HOW (MongoDB in
/// Infrastructure). OrderCompleted carries only EventId (no Artist/VenueName — Ordering's saga
/// pipeline has never had that data), so this store intentionally does not denormalize anything
/// beyond EventId; Commit 4's recommendation query looks up Artist/VenueName from Search's index
/// at read time instead of duplicating it here.
/// </summary>
public interface IPurchaseHistoryStore
{
    /// <summary>Idempotent by construction — recording the same (UserId, EventId) twice does not duplicate the EventId.</summary>
    Task RecordPurchaseAsync(Guid userId, Guid eventId, DateTimeOffset purchasedAt, CancellationToken cancellationToken = default);

    /// <summary>Null when the user has never completed a purchase — the cold-start case Commit 4 falls back to popular for.</summary>
    Task<PurchaseHistoryDocument?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>EventIds ordered by how many distinct users purchased them, most-purchased first — cold-start fallback.</summary>
    Task<IReadOnlyList<Guid>> GetPopularEventIdsAsync(int take, CancellationToken cancellationToken = default);
}
