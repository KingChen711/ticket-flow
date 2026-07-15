using ClickHouse.Client.ADO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Analytics.Application.Abstractions;

namespace TicketFlow.Analytics.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["ClickHouse:ConnectionString"]
            ?? throw new InvalidOperationException("Configuration 'ClickHouse:ConnectionString' was not found.");

        // ClickHouseConnection wraps an HTTP call per command (no persistent TCP session to
        // guard), same reasoning as registering ElasticsearchClient as a singleton elsewhere in
        // Phase 6 — safe to share across concurrent requests.
        services.AddSingleton(new ClickHouseConnection(connectionString));

        services.AddSingleton<ISalesStore, ClickHouseSalesStore>();

        services.AddHealthChecks().AddCheck<ClickHouseHealthCheck>("clickhouse");

        return services;
    }
}
