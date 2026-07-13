using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketFlow.Reservation.Application.Abstractions;
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
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        var redisConnectionString = configuration["Redis:ConnectionString"]
            ?? throw new InvalidOperationException("Configuration 'Redis:ConnectionString' was not found.");

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));
        services.AddScoped<ISeatHoldStore, RedisSeatHoldStore>();

        services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis");

        return services;
    }
}
