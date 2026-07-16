using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace TicketFlow.Recommendation.Infrastructure;

/// <summary>
/// Hand-rolled instead of an AspNetCore.HealthChecks.MongoDb package — a ping command is all a
/// liveness check needs, same reasoning as ElasticsearchHealthCheck (Phase 6 Commit 2)/
/// RedisHealthCheck (Phase 2): no extra package for one check.
/// </summary>
public sealed class MongoHealthCheck(IMongoDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("MongoDB ping succeeded.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB ping failed.", ex);
        }
    }
}
