using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow.Ordering.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
