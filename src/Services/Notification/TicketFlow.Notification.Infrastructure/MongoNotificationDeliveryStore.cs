using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Infrastructure;

public sealed class MongoNotificationDeliveryStore(IMongoDatabase database) : INotificationDeliveryStore
{
    private IMongoCollection<DeliveryDocument> Deliveries =>
        database.GetCollection<DeliveryDocument>("notificationDeliveries");

    public async Task<NotificationDeliveryAttempt> EnsureCreatedAsync(
        NotificationDeliveryRequest request,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DeliveryDocument>.Filter.Eq(document => document.Id, request.DeliveryId);
        var update = Builders<DeliveryDocument>.Update
            .SetOnInsert(document => document.IdempotencyKey, request.IdempotencyKey)
            .SetOnInsert(document => document.Recipient, request.Recipient)
            .SetOnInsert(document => document.Template, request.Template)
            .SetOnInsert(document => document.Subject, request.Subject)
            .SetOnInsert(document => document.Body, request.Body)
            .SetOnInsert(document => document.State, NotificationDeliveryState.Pending.ToString())
            .SetOnInsert(document => document.AttemptCount, 0)
            .SetOnInsert(document => document.CreatedAt, createdAt.UtcDateTime);

        await Deliveries.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
        var document = await Deliveries.Find(filter).SingleAsync(cancellationToken);
        if (document.IdempotencyKey != request.IdempotencyKey || document.Recipient != request.Recipient ||
            document.Template != request.Template)
        {
            throw new InvalidOperationException(
                $"Delivery id '{request.DeliveryId}' is already bound to different content.");
        }

        // Subject/body are deliberately first-writer-wins. Reminder projections can be rescheduled
        // after the provider accepted a message but before RemindedAt was saved; replaying that
        // business operation must use the durable original payload, not reject it forever because
        // mutable event copy changed in the meantime.

        return Map(document);
    }

    public async Task<NotificationDeliveryAttempt?> GetAsync(
        string deliveryId,
        CancellationToken cancellationToken = default)
    {
        var document = await Deliveries.Find(item => item.Id == deliveryId).FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : Map(document);
    }

    public async Task<NotificationDeliveryAttempt?> TryAcquireAsync(
        string deliveryId,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DeliveryDocument>.Filter.And(
            Builders<DeliveryDocument>.Filter.Eq(document => document.Id, deliveryId),
            Builders<DeliveryDocument>.Filter.Ne(document => document.State, NotificationDeliveryState.Delivered.ToString()),
            Builders<DeliveryDocument>.Filter.Or(
                Builders<DeliveryDocument>.Filter.Ne(document => document.State, NotificationDeliveryState.Delivering.ToString()),
                Builders<DeliveryDocument>.Filter.Lte(document => document.LeaseUntil, now.UtcDateTime),
                Builders<DeliveryDocument>.Filter.Eq(document => document.LeaseUntil, null)));
        var update = Builders<DeliveryDocument>.Update
            .Set(document => document.State, NotificationDeliveryState.Delivering.ToString())
            .Set(document => document.LeaseUntil, leaseUntil.UtcDateTime)
            .Set(document => document.LastError, null)
            .Inc(document => document.AttemptCount, 1);

        var document = await Deliveries.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<DeliveryDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        return document is null ? null : Map(document);
    }

    public Task MarkDeliveredAsync(
        string deliveryId,
        string providerReference,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DeliveryDocument>.Filter.Eq(document => document.Id, deliveryId);
        var update = Builders<DeliveryDocument>.Update
            .Set(document => document.State, NotificationDeliveryState.Delivered.ToString())
            .Set(document => document.ProviderReference, providerReference)
            .Set(document => document.DeliveredAt, deliveredAt.UtcDateTime)
            .Set(document => document.LeaseUntil, null)
            .Set(document => document.LastError, null);
        return Deliveries.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
    }

    public Task MarkFailedAsync(
        string deliveryId,
        string error,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DeliveryDocument>.Filter.And(
            Builders<DeliveryDocument>.Filter.Eq(document => document.Id, deliveryId),
            Builders<DeliveryDocument>.Filter.Ne(document => document.State, NotificationDeliveryState.Delivered.ToString()));
        var update = Builders<DeliveryDocument>.Update
            .Set(document => document.State, NotificationDeliveryState.Failed.ToString())
            .Set(document => document.LeaseUntil, null)
            .Set(document => document.LastError, error[..Math.Min(error.Length, 2000)])
            .Set(document => document.LastAttemptAt, failedAt.UtcDateTime);
        return Deliveries.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
    }

    private static NotificationDeliveryAttempt Map(DeliveryDocument document) => new(
        document.Id,
        document.IdempotencyKey,
        document.Recipient,
        document.Template,
        document.Subject,
        document.Body,
        Enum.Parse<NotificationDeliveryState>(document.State),
        document.AttemptCount,
        new DateTimeOffset(document.CreatedAt, TimeSpan.Zero),
        document.LeaseUntil is null ? null : new DateTimeOffset(document.LeaseUntil.Value, TimeSpan.Zero),
        document.ProviderReference,
        document.DeliveredAt is null ? null : new DateTimeOffset(document.DeliveredAt.Value, TimeSpan.Zero),
        document.LastError);

    [BsonIgnoreExtraElements]
    private sealed class DeliveryDocument
    {
        [BsonId]
        public string Id { get; set; } = null!;
        public string IdempotencyKey { get; set; } = null!;
        public string Recipient { get; set; } = null!;
        public string Template { get; set; } = null!;
        public string Subject { get; set; } = null!;
        public string Body { get; set; } = null!;
        public string State { get; set; } = null!;
        public int AttemptCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime? LeaseUntil { get; set; }
        public string? ProviderReference { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public string? LastError { get; set; }
    }
}
