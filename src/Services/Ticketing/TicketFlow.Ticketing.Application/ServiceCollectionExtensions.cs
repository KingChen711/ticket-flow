using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow.Ticketing.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTicketingApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
