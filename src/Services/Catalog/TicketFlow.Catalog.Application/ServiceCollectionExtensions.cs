using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Application.EventDetails;
using TicketFlow.Catalog.Application.Events;
using TicketFlow.Catalog.Application.SeatMaps;
using TicketFlow.Catalog.Application.Venues;
using TicketFlow.Catalog.Application.Workflows;

namespace TicketFlow.Catalog.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<VenueService>();
        services.AddScoped<EventService>();
        services.AddScoped<SeatMapService>();
        services.AddScoped<EventDetailAggregator>();
        services.AddScoped<EventOnboardingWorkflowService>();
        services.AddScoped<EventCancellationWorkflowService>();
        services.AddSingleton<IWorkflowFailureInjector, NoWorkflowFailureInjector>();

        return services;
    }
}
