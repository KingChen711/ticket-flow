using MassTransit;
using TicketFlow.Contracts.Messaging.Notification;

namespace TicketFlow.Notification.Api.Consumers;

/// <summary>
/// Happy-path finale of PurchaseSaga (Commit 4 defined the contract, Commit 9 wires this
/// consumer). Same "log only, no real send" scope as CatalogEventPublishedConsumer (Phase 3) —
/// naturally idempotent, no inbox needed.
/// </summary>
public sealed class NotifyOrderCompletedConsumer(ILogger<NotifyOrderCompletedConsumer> logger) : IConsumer<NotifyOrderCompleted>
{
    public Task Consume(ConsumeContext<NotifyOrderCompleted> context)
    {
        var message = context.Message;
        logger.LogInformation(
            "[notification] gửi 'vé của bạn đã sẵn sàng' cho {CustomerName} <{CustomerEmail}> — order {OrderId}, {TicketCount} vé",
            message.CustomerName,
            message.CustomerEmail,
            message.OrderId,
            message.TicketPdfUrls.Count);

        return Task.CompletedTask;
    }
}
