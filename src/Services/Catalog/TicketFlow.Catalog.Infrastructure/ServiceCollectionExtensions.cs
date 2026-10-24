using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Application.EventDetails;
using TicketFlow.Catalog.Infrastructure.EventDetails;
using TicketFlow.Catalog.Infrastructure.Messaging;
using TicketFlow.Catalog.Infrastructure.Repositories;
using TicketFlow.Catalog.Infrastructure.Workflows;
using TicketFlow.Messaging;
using TicketFlow.Web;

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
        services.AddScoped<IEventOnboardingWorkflowRepository, EventOnboardingWorkflowRepository>();
        services.AddScoped<IEventCancellationWorkflowRepository, EventCancellationWorkflowRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();

        // 3s timeout — secondary sources for the Commit 5 aggregator. Short on purpose: a slow
        // reservation/recommendation call shouldn't stall the primary catalog read for long
        // before graceful degradation (empty list) kicks in.
        var reservationBaseUrl = configuration["Reservation:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Reservation:BaseUrl' was not found.");
        services.AddHttpClient<ISeatAvailabilityClient, HttpSeatAvailabilityClient>(client =>
        {
            client.BaseAddress = new Uri(reservationBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(3);
        });

        // Commit 14: inventory provisioning is a write into another service's durable state, so it
        // now carries a client-credentials service token instead of relying on network reachability.
        // The read-only availability client above deliberately stays anonymous — availability is a
        // public fact the customer web app shows to logged-out visitors.
        services.AddServiceTokenHandler(configuration);
        services.AddHttpClient<IReservationInventoryClient, HttpReservationInventoryClient>(client =>
        {
            client.BaseAddress = new Uri(reservationBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddHttpMessageHandler<ServiceTokenHandler>();

        var recommendationBaseUrl = configuration["Recommendation:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Recommendation:BaseUrl' was not found.");
        services.AddHttpClient<IRecommendationClient, HttpRecommendationClient>(client =>
        {
            client.BaseAddress = new Uri(recommendationBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(3);
        }).AddHttpMessageHandler<ServiceTokenHandler>();

        // AddEntityFrameworkOutbox writes messages published inside a SaveChangesAsync
        // transaction into the outbox_message table instead of hitting RabbitMQ directly — the
        // bus outbox delivery service (hosted automatically by AddMassTransit) drains it after
        // commit. This is why messaging wiring moved here from Program.cs (Commit 1): it must
        // share the same CatalogDbContext as the business writes for the transaction to cover
        // both.
        services.AddTicketFlowMessaging(configuration, "catalog", busConfigurator =>
        {
            busConfigurator.AddConsumer<EventCancellationCompletedConsumer>();
            busConfigurator.AddEntityFrameworkOutbox<CatalogDbContext>(outboxConfigurator =>
            {
                outboxConfigurator.UsePostgres();
                outboxConfigurator.UseBusOutbox();
            });
        });

        return services;
    }
}
