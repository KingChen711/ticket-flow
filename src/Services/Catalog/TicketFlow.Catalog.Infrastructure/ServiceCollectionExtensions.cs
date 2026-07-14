using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Infrastructure.Repositories;
using TicketFlow.Messaging;

namespace TicketFlow.Catalog.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IVenueRepository, VenueRepository>();
        services.AddScoped<ISeatMapRepository, SeatMapRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // AddEntityFrameworkOutbox writes messages published inside a SaveChangesAsync
        // transaction into the outbox_message table instead of hitting RabbitMQ directly — the
        // bus outbox delivery service (hosted automatically by AddMassTransit) drains it after
        // commit. This is why messaging wiring moved here from Program.cs (Commit 1): it must
        // share the same CatalogDbContext as the business writes for the transaction to cover
        // both.
        services.AddTicketFlowMessaging(configuration, busConfigurator =>
        {
            busConfigurator.AddEntityFrameworkOutbox<CatalogDbContext>(outboxConfigurator =>
            {
                outboxConfigurator.UsePostgres();
                outboxConfigurator.UseBusOutbox();
            });
        });

        return services;
    }
}
