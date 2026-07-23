using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Application;

public sealed class NotificationDeliveryService(
    INotificationDeliveryStore store,
    INotificationProvider provider,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan DeliveryLease = TimeSpan.FromMinutes(2);

    public async Task<NotificationDeliveryAttempt> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var now = timeProvider.GetUtcNow();
        var attempt = await store.EnsureCreatedAsync(request, now, cancellationToken);
        if (attempt.State == NotificationDeliveryState.Delivered)
            return attempt;

        attempt = await store.TryAcquireAsync(
            request.DeliveryId,
            now,
            now.Add(DeliveryLease),
            cancellationToken);
        if (attempt is null)
        {
            var current = await store.GetAsync(request.DeliveryId, cancellationToken);
            if (current?.State == NotificationDeliveryState.Delivered)
                return current;

            throw new NotificationDeliveryInProgressException(request.DeliveryId);
        }

        try
        {
            var confirmation = await provider.SendAsync(
                new ProviderNotification(
                    attempt.IdempotencyKey,
                    attempt.Recipient,
                    attempt.Template,
                    attempt.Subject,
                    attempt.Body),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(confirmation.ProviderReference))
                throw new InvalidOperationException("The delivery provider did not return a message reference.");

            await store.MarkDeliveredAsync(
                attempt.DeliveryId,
                confirmation.ProviderReference,
                timeProvider.GetUtcNow(),
                cancellationToken);
            return (await store.GetAsync(attempt.DeliveryId, cancellationToken))!;
        }
        catch (Exception exception)
        {
            // Independent token: a caller disconnect after the provider call must not leave the
            // attempt leased forever. If the provider accepted it, retry uses the same key.
            await store.MarkFailedAsync(
                attempt.DeliveryId,
                exception.Message,
                timeProvider.GetUtcNow(),
                CancellationToken.None);
            throw;
        }
    }

    private static void Validate(NotificationDeliveryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeliveryId) || request.DeliveryId.Length > 200)
            throw new ArgumentException("Delivery id is required and cannot exceed 200 characters.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("Provider idempotency key is required and cannot exceed 200 characters.");
        if (string.IsNullOrWhiteSpace(request.Recipient))
            throw new ArgumentException("Delivery recipient is required.");
        if (string.IsNullOrWhiteSpace(request.Template))
            throw new ArgumentException("Delivery template is required.");
    }
}

public sealed class NotificationDeliveryInProgressException(string deliveryId)
    : Exception($"Notification delivery '{deliveryId}' is already being attempted.");
