using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace TicketFlow.Recommendation.Infrastructure;

/// <summary>
/// Establishes the one-document-per-user invariant before consumers begin processing messages.
/// Existing duplicate documents must be reconciled manually; silently choosing an owner/document
/// would discard purchase history and is expressly outside the expand-only migration.
/// </summary>
public sealed class PurchaseHistoryIndexInitializer(IMongoDatabase database) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>("purchaseHistory");
        var index = new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("UserId"),
            new CreateIndexOptions { Name = "ux_purchase_history_user_id", Unique = true });

        await collection.Indexes.CreateOneAsync(index, cancellationToken: cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
