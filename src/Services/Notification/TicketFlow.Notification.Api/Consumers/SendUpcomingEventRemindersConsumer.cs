using MassTransit;
using Microsoft.Extensions.Options;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Notification.Application;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Api.Consumers;

/// <summary>
/// The one consumer that owns "who needs reminding" (docs/phase-9-plan.md mục 5 — EventReminderTimer
/// Lambda is deliberately thin, all of this logic lives here instead). Uses the tick's own
/// TriggeredAt as "now" for the window, not this process's clock — the EventBridge schedule is the
/// authoritative time signal for a scheduled system, not whichever consumer instance happens to
/// pick up the message.
///
/// TryMarkRemindedAsync's return value IS the idempotency check: a redelivered/duplicate tick (or
/// two ticks racing before the window's recipients are all processed) finds ModifiedCount == 0 for
/// anything already marked and skips logging it again — no separate inbox needed, same "the write
/// itself is the dedup" pattern AddToSet gave Recommendation's purchase history (Phase 6 Commit 3).
/// </summary>
public sealed class SendUpcomingEventRemindersConsumer(
    IEventReminderReadModel readModel,
    NotificationDeliveryService deliveries,
    IOptions<ReminderOptions> reminderOptions,
    ILogger<SendUpcomingEventRemindersConsumer> logger) : IConsumer<SendUpcomingEventReminders>
{
    public async Task Consume(ConsumeContext<SendUpcomingEventReminders> context)
    {
        var now = context.Message.TriggeredAt;
        var windowEnd = now.AddHours(reminderOptions.Value.WindowHours);

        var pending = await readModel.GetPendingRemindersAsync(now, windowEnd, context.CancellationToken);

        foreach (var reminder in pending)
        {
            await deliveries.DeliverAsync(
                new NotificationDeliveryRequest(
                    $"reminder:{reminder.OrderId:N}",
                    $"reminder:{reminder.OrderId:N}",
                    reminder.CustomerEmail,
                    "upcoming-event-reminder",
                    $"Nhắc lịch: {reminder.EventTitle}",
                    $"{reminder.CustomerName}, sự kiện {reminder.EventTitle} ({reminder.EventArtist} @ {reminder.EventVenueName}) bắt đầu lúc {reminder.StartsAt:O}."),
                context.CancellationToken);

            // Business completion follows provider confirmation. A crash between these two writes
            // replays the already-delivered attempt without sending a second provider message.
            var firstTimeReminded = await readModel.TryMarkRemindedAsync(
                reminder.OrderId,
                now,
                context.CancellationToken);
            if (!firstTimeReminded)
                continue;

            logger.LogInformation(
                "[notification] nhắc {CustomerName} <{CustomerEmail}> — sự kiện '{EventTitle}' ({Artist} @ {VenueName}) bắt đầu lúc {StartsAt}, order {OrderId}",
                reminder.CustomerName,
                reminder.CustomerEmail,
                reminder.EventTitle,
                reminder.EventArtist,
                reminder.EventVenueName,
                reminder.StartsAt,
                reminder.OrderId);
        }
    }
}
