namespace TicketFlow.Contracts.Messaging.Notification;

/// <summary>
/// Command sent by the PurchaseSaga as its final step, right after publishing
/// <see cref="Ordering.OrderCompleted"/>. The notification service consumer (wired in Commit 9)
/// sends the "your tickets are ready" email/push. No consumer exists yet as of Commit 4 — the
/// saga publishes it into the void until Commit 9, same "contract before consumer" ordering used
/// throughout Phase 5.
/// </summary>
public sealed record NotifyOrderCompleted(
    Guid OrderId,
    Guid UserId,
    string CustomerName,
    string CustomerEmail,
    IReadOnlyList<string> TicketPdfUrls);
