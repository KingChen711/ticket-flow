namespace TicketFlow.Notification.Application.Abstractions;

/// <summary>
/// Reminder read model (docs/phase-9-plan.md Phase 9 Commit 4, mục 6) — "lịch event + attendee +
/// cờ đã-nhắc" split across two logical concerns the port exposes as one cohesive surface: event
/// schedule (populated from Catalog's Published/Rescheduled/Cancelled events) and reminder
/// recipients (populated from Ordering's NotifyOrderCompleted). The infrastructure adapter is
/// free to store these as one or two Mongo collections — application code only needs the shape
/// below, not the storage layout.
/// </summary>
/// <remarks>
/// Commit 11 made the three schedule-writing methods revision-guarded and self-creating, for the
/// same reason as Search's IEventSearchIndex (see its doc comment): the old shape did an
/// unconditional write for Published and an update-only (no create) write for
/// Rescheduled/Cancelled, so an out-of-order Published could resurrect a cancelled event's
/// schedule, and a Rescheduled/Cancelled that overtook Published found no document to update and
/// silently lost the fact. Every apply now creates the document if missing and is rejected by a
/// revision that is not at least as high as what is already stored — except Cancel, which is a
/// permanent tombstone: Catalog's domain forbids ever un-cancelling an event, so once recorded
/// here it wins forever regardless of the revision on a later message.
/// </remarks>
public interface IEventReminderReadModel
{
    Task ApplyPublishedAsync(
        Guid catalogEventId,
        string title,
        string artist,
        string venueName,
        DateTimeOffset startsAt,
        long revision,
        CancellationToken cancellationToken = default);

    Task ApplyRescheduledAsync(
        Guid catalogEventId,
        DateTimeOffset startsAt,
        long revision,
        CancellationToken cancellationToken = default);

    Task ApplyCancelledAsync(Guid catalogEventId, long revision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upsert by OrderId (one recipient per order, not per seat — a customer buying 3 seats for
    /// the same event gets one reminder, not three). Must not clear an already-set RemindedAt on
    /// a redelivered NotifyOrderCompleted (MassTransit's own retry can redeliver the same command)
    /// — the adapter is responsible for that idempotency, not the caller.
    /// </summary>
    Task AddRecipientAsync(
        Guid orderId,
        Guid catalogEventId,
        Guid userId,
        string customerName,
        string customerEmail,
        CancellationToken cancellationToken = default);

    /// <summary>Recipients not yet reminded, whose event starts within [windowStart, windowEnd]
    /// and isn't cancelled.</summary>
    Task<IReadOnlyList<UpcomingReminder>> GetPendingRemindersAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default);

    /// <returns>true the first time this order is marked reminded; false if it was already
    /// marked (by an earlier tick) — this return value IS the idempotency guarantee the caller
    /// relies on to decide whether to actually log the reminder.</returns>
    Task<bool> TryMarkRemindedAsync(Guid orderId, DateTimeOffset remindedAt, CancellationToken cancellationToken = default);
}

public sealed record UpcomingReminder(
    Guid OrderId,
    Guid CatalogEventId,
    Guid UserId,
    string CustomerName,
    string CustomerEmail,
    string EventTitle,
    string EventArtist,
    string EventVenueName,
    DateTimeOffset StartsAt);
