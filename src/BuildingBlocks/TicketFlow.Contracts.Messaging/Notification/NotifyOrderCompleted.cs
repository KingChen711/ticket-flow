namespace TicketFlow.Contracts.Messaging.Notification;

/// <summary>
/// Command sent by the PurchaseSaga as its final step, right after publishing
/// <see cref="Ordering.OrderCompleted"/>. The notification service consumer sends the "your
/// tickets are ready" email/push, and (Phase 9 Commit 4) records the buyer as a reminder
/// recipient for that event.
///
/// <c>EventId</c> added additively in Phase 9 Commit 4 — same precedent as Phase 6 adding a
/// field to <see cref="Catalog.CatalogEventPublished"/>. Without it, the reminder read model has
/// no way to associate a purchase with the event it's for; the saga already carries
/// <c>CatalogEventId</c> in its state, so this is a zero-cost addition at the publish site.
/// </summary>
public sealed record NotifyOrderCompleted(
    Guid OrderId,
    Guid UserId,
    Guid EventId,
    string CustomerName,
    string CustomerEmail,
    IReadOnlyList<string> TicketPdfUrls);
