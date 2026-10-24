using JasperFx;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Infrastructure.Messaging;
using TicketFlow.Ordering.Infrastructure.Pricing;

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
        services.AddScoped<IOrderViewStore, MartenOrderViewStore>();
        services.AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();

        var catalogBaseUrl = configuration["Catalog:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Catalog:BaseUrl' was not found.");
        var reservationBaseUrl = configuration["Reservation:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Reservation:BaseUrl' was not found.");
        services.AddHttpClient(HttpSeatPricingSource.CatalogClient, client =>
        {
            client.BaseAddress = new Uri(catalogBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddHttpClient(HttpSeatPricingSource.ReservationClient, client =>
        {
            client.BaseAddress = new Uri(reservationBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddScoped<ISeatPricingSource, HttpSeatPricingSource>();

        services.AddHealthChecks().AddCheck<MartenHealthCheck>("marten");

        return services;
    }
}
