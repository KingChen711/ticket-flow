using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Payment.Application.Payments;

namespace TicketFlow.Payment.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PaymentProcessingService>();

        return services;
    }
}
