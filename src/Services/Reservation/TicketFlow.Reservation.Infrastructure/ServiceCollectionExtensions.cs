using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Infrastructure.Reconciliation;
using TicketFlow.Reservation.Infrastructure.Redis;

namespace TicketFlow.Reservation.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Postgres (durable seat inventory) + Redis (live hold store) wiring. ISeatHoldStore now
    /// has a real registration (RedisSeatHoldStore), which is what actually unblocks
    /// AddReservationApplication() from being safe to call — its services no longer hit an
    /// unresolvable dependency during ASP.NET Core's DI validation at Build() (the trap hit in
    /// Identity Commit 11). Program.cs still doesn't call AddReservationApplication() yet
    /// though — kept deferred to Commit 7 to match the Catalog pattern (Application wiring
    /// lands together with the REST endpoints that actually use it), not because it's forced.
    /// </summary>
    public static IServiceCollection AddReservationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddDbContext<ReservationDbContext>(options =>
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

        services.AddScoped<ISeatRepository, SeatRepository>();
        services.AddScoped<InventoryCleanupService>();
        services.AddScoped<IEventSalePolicyRepository, EventSalePolicyRepository>();
        services.AddScoped<IHoldQuoteRepository, HoldQuoteRepository>();
        services.AddScoped<ISeatAllocationRepository, SeatAllocationRepository>();
        services.AddScoped<IReservationOperationReceiptRepository, ReservationOperationReceiptRepository>();
        services.AddScoped<IAllocationGenerationSource, PostgresAllocationGenerationSource>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISeatCacheReconciler, SeatCacheReconciler>();
        services.AddScoped<ISeatCacheBootstrapper, SeatCacheBootstrapper>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        var redisConnectionString = configuration["Redis:ConnectionString"]
            ?? throw new InvalidOperationException("Configuration 'Redis:ConnectionString' was not found.");

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));
        services.AddScoped<ISeatHoldStore, RedisSeatHoldStore>();

        services.AddHealthChecks().AddCheck<RedisHealthCheck>(
            "redis", HealthStatus.Unhealthy, ["ready"], TimeSpan.FromSeconds(3));

        return services;
    }
}
