namespace TicketFlow.Notification.Application.Abstractions;

public enum NotificationDeliveryState
{
    Pending,
    Delivering,
    Delivered,
    Failed,
}

public sealed record NotificationDeliveryRequest(
    string DeliveryId,
    string IdempotencyKey,
    string Recipient,
    string Template,
    string Subject,
    string Body);

public sealed record NotificationDeliveryAttempt(
    string DeliveryId,
    string IdempotencyKey,
    string Recipient,
    string Template,
    string Subject,
    string Body,
    NotificationDeliveryState State,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LeaseUntil,
    string? ProviderReference,
    DateTimeOffset? DeliveredAt,
    string? LastError);

public interface INotificationDeliveryStore
{
    Task<NotificationDeliveryAttempt> EnsureCreatedAsync(
        NotificationDeliveryRequest request,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<NotificationDeliveryAttempt?> GetAsync(
        string deliveryId,
        CancellationToken cancellationToken = default);

    Task<NotificationDeliveryAttempt?> TryAcquireAsync(
        string deliveryId,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken = default);

    Task MarkDeliveredAsync(
        string deliveryId,
        string providerReference,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        string deliveryId,
        string error,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default);
}

public sealed record ProviderNotification(
    string IdempotencyKey,
    string Recipient,
    string Template,
    string Subject,
    string Body);

public sealed record ProviderDeliveryConfirmation(string ProviderReference);

public interface INotificationProvider
{
    Task<ProviderDeliveryConfirmation> SendAsync(
        ProviderNotification notification,
        CancellationToken cancellationToken = default);
}
