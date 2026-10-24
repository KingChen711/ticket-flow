using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Api.Consumers;

/// <summary>
/// New in Phase 9 Commit 4 — no consumer existed for this event before (mục 6 lists it as one of
/// the 4 consumers the reminder read model needs). Keeps a rescheduled event's already-recorded
/// attendees pointed at the right StartsAt, so a reminder that fires after a reschedule reflects
/// the new time, not the stale one. Revision-guarded and self-creating since Commit 11 — before
/// this, a Reschedule that overtook Published found no document to update and silently lost the
/// new StartsAt forever.
/// </summary>
public sealed class CatalogEventRescheduledConsumer(
    IEventReminderReadModel readModel,
    ILogger<CatalogEventRescheduledConsumer> logger) : IConsumer<CatalogEventRescheduled>
{
    public async Task Consume(ConsumeContext<CatalogEventRescheduled> context)
    {
        var message = context.Message;

        await readModel.ApplyRescheduledAsync(
            message.CatalogEventId, message.StartsAt, message.AggregateVersion, context.CancellationToken);

        logger.LogInformation(
            "[notification] event {CatalogEventId} đổi lịch, StartsAt mới {StartsAt}",
            message.CatalogEventId,
            message.StartsAt);
    }
}
