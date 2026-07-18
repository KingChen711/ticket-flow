using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Api.Consumers;

/// <summary>
/// Phase 3 stub for the "will send" log; Phase 9 Commit 4 adds the real side effect this consumer
/// was always going to need once it started writing state (see docs/phase-3-plan.md "Hướng mở
/// rộng") — upserting the event's schedule into the reminder read model, the "lịch" half of
/// mục 6. Naturally idempotent same as before: a redelivered CatalogEventPublished just upserts
/// the same schedule fields again.
/// </summary>
public sealed class CatalogEventPublishedConsumer(
    IEventReminderReadModel readModel,
    ILogger<CatalogEventPublishedConsumer> logger) : IConsumer<CatalogEventPublished>
{
    public async Task Consume(ConsumeContext<CatalogEventPublished> context)
    {
        var message = context.Message;

        await readModel.UpsertEventScheduleAsync(
            message.CatalogEventId,
            message.Title,
            message.Artist,
            message.VenueName,
            message.StartsAt,
            context.CancellationToken);

        logger.LogInformation(
            "[notification] sẽ gửi thông báo cho event {Title} ({CatalogEventId})",
            message.Title,
            message.CatalogEventId);
    }
}
