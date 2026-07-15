namespace TicketFlow.Recommendation.Application.Recommendations;

public sealed record RecommendedEvent(
    Guid EventId,
    string Title,
    string Artist,
    string VenueName,
    string City,
    DateTimeOffset StartsAt);
