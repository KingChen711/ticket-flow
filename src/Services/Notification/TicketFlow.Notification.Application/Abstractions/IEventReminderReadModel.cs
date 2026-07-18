namespace TicketFlow.Notification.Application.Abstractions;

/// <summary>
/// Reminder read model (docs/phase-9-plan.md Phase 9 Commit 4, mục 6) — "lịch event + attendee +
/// cờ đã-nhắc" split across two logical concerns the port exposes as one cohesive surface: event
/// schedule (populated from Catalog's Published/Rescheduled/Cancelled events) and reminder
/// recipients (populated from Ordering's NotifyOrderCompleted). The infrastructure adapter is
/// free to store these as one or two Mongo collections — application code only needs the shape
/// below, not the storage layout.
/// </summary>
public interface IEventReminderReadModel
{
    Task UpsertEventScheduleAsync(
        Guid catalogEventId,
        string title,
        string artist,
        string venueName,
        DateTimeOffset startsAt,
        CancellationToken cancellationToken = default);

    Task UpdateEventStartsAtAsync(Guid catalogEventId, DateTimeOffset startsAt, CancellationToken cancellationToken = default);

    Task MarkEventCancelledAsync(Guid catalogEventId, CancellationToken cancellationToken = default);

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
