using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TicketFlow.Recommendation.Application.Abstractions;
using TicketFlow.Recommendation.Application.Purchases;

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
            .Max(d => d.LastPurchasedAt, purchasedAt)
            .SetOnInsert(d => d.UserId, userId);

        try
        {
            await collection.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Two first purchases for one new user can both observe "no document" and attempt an
            // upsert insert. ux_purchase_history_user_id chooses the winner; the loser must merge
            // into that document without upsert instead of losing its event or poisoning the
            // MassTransit delivery. $addToSet keeps the retry business-idempotent.
            var retry = await collection.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = false },
                cancellationToken);

            if (retry.MatchedCount != 1)
                throw;
        }
    }

    public async Task<PurchaseHistoryDocument?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var collection = database.GetCollection<PurchaseHistoryBsonDocument>(CollectionName);
        var filter = Builders<PurchaseHistoryBsonDocument>.Filter.Eq(d => d.UserId, userId);
        var document = await collection.Find(filter).FirstOrDefaultAsync(cancellationToken);

        return document is null
            ? null
            : new PurchaseHistoryDocument(document.UserId, document.PurchasedEventIds, document.LastPurchasedAt);
    }

    /// <summary>
    /// Unwind + group + sort-desc + limit — confirmed via a real probe against a live mongo:7
    /// container (seeded 3 users purchasing eventA/B/C with different overlap) that the typed
    /// fluent Aggregate API's Group() stage needs a raw BsonDocument (no typed Group overload fits
    /// "count occurrences after Unwind"), and that <c>doc["_id"].AsGuid</c> correctly round-trips
    /// back to the original Guid once GuidRepresentation.Standard is registered.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetPopularEventIdsAsync(int take, CancellationToken cancellationToken = default)
    {
        var collection = database.GetCollection<PurchaseHistoryBsonDocument>(CollectionName);

        var groups = await collection.Aggregate()
            .Unwind(d => d.PurchasedEventIds)
            .Group(new BsonDocument
            {
                { "_id", "$PurchasedEventIds" },
                { "count", new BsonDocument("$sum", 1) },
            })
            .SortByDescending(d => d["count"])
            .Limit(take)
            .ToListAsync(cancellationToken);

        return groups.Select(d => d["_id"].AsGuid).ToList();
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
