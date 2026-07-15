using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TicketFlow.Recommendation.Application.Abstractions;

namespace TicketFlow.Recommendation.Infrastructure;

public sealed class MongoPurchaseHistoryStore(IMongoDatabase database) : IPurchaseHistoryStore
{
    private const string CollectionName = "purchaseHistory";

    public async Task RecordPurchaseAsync(Guid userId, Guid eventId, DateTimeOffset purchasedAt,
        CancellationToken cancellationToken = default)
    {
        var collection = database.GetCollection<PurchaseHistoryBsonDocument>(CollectionName);

        var filter = Builders<PurchaseHistoryBsonDocument>.Filter.Eq(d => d.UserId, userId);

        // AddToSet is MongoDB-native set-uniqueness — a redelivered/duplicate OrderCompleted for
        // the same (UserId, EventId) does not append a second copy. Confirmed via a real probe:
        // 3 upserts (2 distinct events + 1 redelivery) left exactly 2 elements in the array.
        var update = Builders<PurchaseHistoryBsonDocument>.Update
            .AddToSet(d => d.PurchasedEventIds, eventId)
            .Set(d => d.LastPurchasedAt, purchasedAt)
            .SetOnInsert(d => d.UserId, userId);

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>
    /// Mongo auto-assigns an ObjectId _id on upsert-insert; without BsonIgnoreExtraElements the
    /// driver throws FormatException on read-back ("Element '_id' does not match any field or
    /// property") since this type has no matching property — confirmed via the same probe.
    /// </summary>
    [BsonIgnoreExtraElements]
    private sealed class PurchaseHistoryBsonDocument
    {
        public Guid UserId { get; set; }
        public List<Guid> PurchasedEventIds { get; set; } = [];
        public DateTimeOffset LastPurchasedAt { get; set; }
    }
}
