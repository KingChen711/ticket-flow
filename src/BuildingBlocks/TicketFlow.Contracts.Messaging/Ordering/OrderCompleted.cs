namespace TicketFlow.Contracts.Messaging.Ordering;

/// <summary>
/// Integration event published when the PurchaseSaga completes successfully
/// (seats confirmed → payment processed → tickets issued).
/// Notification service subscribes to send the "your tickets" email/push.
/// </summary>
public sealed record OrderCompleted(
    Guid OrderId,
    Guid UserId,
    Guid EventId,
    IReadOnlyList<Guid> SeatIds,
    IReadOnlyList<string> TicketPdfUrls,
    DateTimeOffset CompletedAt,
    long AggregateVersion = 0,
    DateTimeOffset? OccurredAt = null);
