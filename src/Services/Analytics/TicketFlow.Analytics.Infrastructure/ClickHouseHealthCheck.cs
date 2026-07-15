using ClickHouse.Client.ADO;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TicketFlow.Analytics.Infrastructure;

/// <summary>
/// Hand-rolled instead of a dedicated health-check package — a trivial query is all a liveness
/// check needs, same reasoning as ElasticsearchHealthCheck/MongoHealthCheck elsewhere in Phase 6.
/// </summary>
public sealed class ClickHouseHealthCheck(ClickHouseConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("ClickHouse query succeeded.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("ClickHouse query failed.", ex);
        }
    }
}
