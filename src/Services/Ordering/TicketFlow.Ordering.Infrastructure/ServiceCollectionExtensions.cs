using JasperFx;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddMarten(options =>
        {
            options.Connection(connectionString);
            // Dev-only, same spirit as EF's migrate-on-startup elsewhere in this repo — a real
            // deployment would run schema setup as a separate CI/CD step instead.
            options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

            // Inline = read model updates in the SAME transaction as the event append, so a
            // query right after a command sees the new state immediately (read-after-write
            // consistency) — simplest option for Phase 4's DoD. Async/daemon projections
            // (eventual consistency, higher throughput) are a documented backlog item, not
            // needed here.
            options.Projections.Add<MyOrdersProjection>(ProjectionLifecycle.Inline);
        });

        services.AddScoped<IOrderStore, MartenOrderStore>();

        services.AddHealthChecks().AddCheck<MartenHealthCheck>("marten");

        return services;
    }
}
