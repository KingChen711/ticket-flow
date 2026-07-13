using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Catalog.Application.Events;
using TicketFlow.Catalog.Application.SeatMaps;
using TicketFlow.Catalog.Application.Venues;

namespace TicketFlow.Catalog.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<VenueService>();
        services.AddScoped<EventService>();
        services.AddScoped<SeatMapService>();

        return services;
    }
}
