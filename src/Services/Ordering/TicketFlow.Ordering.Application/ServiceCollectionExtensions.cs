using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Ordering.Application.Orders;

namespace TicketFlow.Ordering.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        // Registered as concrete types, not via ICommandHandler<,> — endpoints inject the
        // specific handler directly (no generic resolve-by-command-type dispatch, see
        // ICommandHandler's doc comment).
        services.AddScoped<PlaceOrderCommandHandler>();
        services.AddScoped<ConfirmOrderCommandHandler>();
        services.AddScoped<CancelOrderCommandHandler>();

        return services;
    }
}
