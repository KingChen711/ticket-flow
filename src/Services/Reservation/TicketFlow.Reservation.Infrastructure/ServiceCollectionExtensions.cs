using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Reservation.Application.Abstractions;

namespace TicketFlow.Reservation.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Postgres side only (durable seat inventory). Does NOT register ISeatHoldStore — that's
    /// the Redis side, wired separately once its implementation exists (Commit 4+). Registering
    /// it here before then would mean AddReservationApplication() can't resolve its
    /// dependencies, and ASP.NET Core's DI validation would fail the app at startup.
    /// </summary>
    public static IServiceCollection AddReservationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddDbContext<ReservationDbContext>(options =>
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

        services.AddScoped<ISeatRepository, SeatRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}
