using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Hand-rolled instead of pulling in AspNetCore.HealthChecks.Redis — StackExchange.Redis's own
/// PingAsync() is all a liveness check needs, no extra package for one check.
/// </summary>
public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis responded in {latency.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis ping failed.", ex);
        }
    }
}
