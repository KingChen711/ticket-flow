using MongoDB.Bson;
using MongoDB.Driver;
using TicketFlow.Phase12.HardeningTests.Infrastructure;
using TicketFlow.Recommendation.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Projections;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class RecommendationPurchaseHistoryConcurrencyTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_first_purchases_merge_once_into_one_user_document()
    {
        var database = environment.RecommendationDatabase;
        var initializer = new PurchaseHistoryIndexInitializer(database);
        await initializer.StartAsync(CancellationToken.None);

        var store = new MongoPurchaseHistoryStore(database);
        var userId = Guid.NewGuid();
        var eventIds = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        var first = DateTimeOffset.UtcNow;

        // Every event is delivered twice using distinct calls. Several writers race on the first
        // upsert; duplicate-key losers must retry as a merge, while $addToSet removes semantic
        // redeliveries of the same event.
        await Task.WhenAll(eventIds.SelectMany((eventId, index) => new[]
        {
            store.RecordPurchaseAsync(userId, eventId, first.AddSeconds(index)),
            store.RecordPurchaseAsync(userId, eventId, first.AddSeconds(index)),
        }));

        var history = Assert.IsType<TicketFlow.Recommendation.Application.Purchases.PurchaseHistoryDocument>(
            await store.GetAsync(userId));
        Assert.Equal(eventIds.Order(), history.PurchasedEventIds.Order());
        Assert.Equal(first.AddSeconds(eventIds.Length - 1), history.LastPurchasedAt, TimeSpan.FromMilliseconds(1));

        var documents = await database.GetCollection<BsonDocument>("purchaseHistory")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .ToListAsync();
        Assert.Single(documents);
    }
}
