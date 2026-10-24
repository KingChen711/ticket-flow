using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Workflows;
using TicketFlow.Messaging;
using TicketFlow.Notification.Application;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Api.Consumers;

public sealed class NotifyOrderCancelledConsumer(NotificationDeliveryService deliveries)
    : IConsumer<OrderCancelledIntegrationEvent>
{
    public async Task Consume(ConsumeContext<OrderCancelledIntegrationEvent> context)
    {
        var message = context.Message;
        if (message.CancellationWorkflowId is not { } workflowId)
            return;
        if (message.CatalogEventId is not { } eventId || eventId == Guid.Empty)
            throw new PermanentMessageFaultException(
                $"Event cancellation '{workflowId}' for order '{message.OrderId}' has no CatalogEventId.");
        if (string.IsNullOrWhiteSpace(message.CustomerEmail))
            throw new PermanentMessageFaultException(
                $"Event cancellation '{workflowId}' for order '{message.OrderId}' has no customer email.");

        var delivered = await deliveries.DeliverAsync(
            new NotificationDeliveryRequest(
                $"event-cancelled:{workflowId:N}:{message.OrderId:N}",
                $"event-cancelled:{workflowId:N}:{message.OrderId:N}",
                message.CustomerEmail,
                "event-cancelled",
                "Sự kiện đã bị hủy",
                $"{message.CustomerName ?? "Khách hàng"}, order {message.OrderId} đã được hủy: {message.Reason}"),
            context.CancellationToken);

        if (delivered.ProviderReference is null || delivered.DeliveredAt is null)
            throw new InvalidOperationException(
                $"Notification delivery '{delivered.DeliveryId}' returned without provider confirmation.");

        await context.Publish(new CustomerNotifiedOfEventCancellation(
            workflowId,
            eventId,
            message.OrderId,
            delivered.ProviderReference,
            delivered.DeliveredAt.Value));
    }
}
