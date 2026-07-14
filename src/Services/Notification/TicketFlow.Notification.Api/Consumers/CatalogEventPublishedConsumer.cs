using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;

namespace TicketFlow.Notification.Api.Consumers;

/// <summary>
/// Phase 3 stub — logs intent only, no real send. Naturally idempotent (a log line has no side
/// effect to duplicate), so no inbox/dedup needed yet. Real sending + inbox lands whenever this
/// consumer starts writing state (see docs/phase-3-plan.md "Hướng mở rộng").
/// </summary>
public sealed class CatalogEventPublishedConsumer(ILogger<CatalogEventPublishedConsumer> logger) : IConsumer<CatalogEventPublished>
{
    public Task Consume(ConsumeContext<CatalogEventPublished> context)
    {
        var message = context.Message;
        logger.LogInformation(
            "[notification] sẽ gửi thông báo cho event {Title} ({CatalogEventId})",
            message.Title,
            message.CatalogEventId);

        return Task.CompletedTask;
    }
}
