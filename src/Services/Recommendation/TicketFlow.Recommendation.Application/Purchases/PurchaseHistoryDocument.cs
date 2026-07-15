namespace TicketFlow.Recommendation.Application.Purchases;

public sealed record PurchaseHistoryDocument(
    Guid UserId,
    IReadOnlyList<Guid> PurchasedEventIds,
    DateTimeOffset LastPurchasedAt);
