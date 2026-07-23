using MassTransit;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Notification.Application;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Api.Consumers;

/// <summary>
/// Happy-path finale of PurchaseSaga (Phase 5 Commit 4 defined the contract, Commit 9 wired this
/// consumer). Phase 9 Commit 4 adds the real side effect — recording the buyer as a reminder
/// recipient for their event, the "attendee" half of mục 6. AddRecipientAsync is upsert-safe on
/// its own (see IEventReminderReadModel's doc comment), so this stays naturally idempotent
/// against a redelivered NotifyOrderCompleted same as before, no inbox needed.
/// </summary>
public sealed class NotifyOrderCompletedConsumer(
    IEventReminderReadModel readModel,
    NotificationDeliveryService deliveries,
    ILogger<NotifyOrderCompletedConsumer> logger) : IConsumer<NotifyOrderCompleted>
{
    public async Task Consume(ConsumeContext<NotifyOrderCompleted> context)
    {
        var message = context.Message;

        await readModel.AddRecipientAsync(
            message.OrderId,
            message.EventId,
            message.UserId,
            message.CustomerName,
            message.CustomerEmail,
            context.CancellationToken);

        await deliveries.DeliverAsync(
            new NotificationDeliveryRequest(
                $"order-completed:{message.OrderId:N}",
                $"order-completed:{message.OrderId:N}",
                message.CustomerEmail,
                "tickets-ready",
                "Vé của bạn đã sẵn sàng",
                $"{message.CustomerName}, {message.TicketPdfUrls.Count} vé cho order {message.OrderId} đã sẵn sàng."),
            context.CancellationToken);

        logger.LogInformation(
            "[notification] gửi 'vé của bạn đã sẵn sàng' cho {CustomerName} <{CustomerEmail}> — order {OrderId}, {TicketCount} vé",
            message.CustomerName,
            message.CustomerEmail,
            message.OrderId,
            message.TicketPdfUrls.Count);
    }
}
