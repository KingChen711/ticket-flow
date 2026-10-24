using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Api.Consumers;

/// <summary>
/// New in Phase 9 Commit 4 (mục 6). Marks the event cancelled in the read model so
/// SendUpcomingEventRemindersConsumer's window query excludes it — nobody gets reminded to
/// attend an event that no longer exists. Permanent tombstone since Commit 11: Catalog's domain
/// forbids ever un-cancelling an event, so this can never be legitimately reversed by a later
/// Published/Rescheduled, whatever revision that message happens to carry.
/// </summary>
public sealed class CatalogEventCancelledConsumer(
    IEventReminderReadModel readModel,
    ILogger<CatalogEventCancelledConsumer> logger) : IConsumer<CatalogEventCancelled>
{
    public async Task Consume(ConsumeContext<CatalogEventCancelled> context)
    {
        var message = context.Message;

        await readModel.ApplyCancelledAsync(message.CatalogEventId, message.AggregateVersion, context.CancellationToken);

        logger.LogInformation("[notification] event {CatalogEventId} bị huỷ, ngừng nhắc lịch", message.CatalogEventId);
    }
}
