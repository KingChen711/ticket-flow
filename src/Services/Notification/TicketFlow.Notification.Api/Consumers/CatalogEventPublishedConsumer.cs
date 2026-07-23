using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Api.Consumers;

/// <summary>
/// Phase 3 stub for the "will send" log; Phase 9 Commit 4 adds the real side effect this consumer
/// was always going to need once it started writing state (see docs/phase-3-plan.md "Hướng mở
/// rộng") — upserting the event's schedule into the reminder read model, the "lịch" half of
/// mục 6. Revision-guarded since Commit 11 (see IEventReminderReadModel's doc comment): a
/// redelivered or out-of-order CatalogEventPublished can no longer resurrect an event this read
/// model already knows was cancelled.
/// </summary>
public sealed class CatalogEventPublishedConsumer(
    IEventReminderReadModel readModel,
    ILogger<CatalogEventPublishedConsumer> logger) : IConsumer<CatalogEventPublished>
{
    public async Task Consume(ConsumeContext<CatalogEventPublished> context)
    {
        var message = context.Message;

        await readModel.ApplyPublishedAsync(
            message.CatalogEventId,
            message.Title,
            message.Artist,
            message.VenueName,
            message.StartsAt,
            message.AggregateVersion,
            context.CancellationToken);

        logger.LogInformation(
            "[notification] sẽ gửi thông báo cho event {Title} ({CatalogEventId})",
            message.Title,
            message.CatalogEventId);
    }
}
