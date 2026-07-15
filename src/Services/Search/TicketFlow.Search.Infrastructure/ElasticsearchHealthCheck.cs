using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TicketFlow.Search.Infrastructure;

/// <summary>
/// Hand-rolled instead of an AspNetCore.HealthChecks.Elasticsearch package — PingAsync() is all
/// a liveness check needs, same reasoning as RedisHealthCheck (Phase 2)/MartenHealthCheck
/// (Phase 4): no extra package for one check.
/// </summary>
public sealed class ElasticsearchHealthCheck(ElasticsearchClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.PingAsync(cancellationToken);
            return response.IsValidResponse
                ? HealthCheckResult.Healthy("Elasticsearch ping succeeded.")
                : HealthCheckResult.Unhealthy($"Elasticsearch ping failed: {response.DebugInformation}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Elasticsearch ping failed.", ex);
        }
    }
}
