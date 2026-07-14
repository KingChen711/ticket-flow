using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow.Payment.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
