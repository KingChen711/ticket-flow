using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Ordering.Application.Orders;

namespace TicketFlow.Ordering.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        // Registered as concrete types, not via ICommandHandler<,>/IQueryHandler<,> — endpoints
        // inject the specific handler directly (no generic resolve-by-type dispatch, see
        // ICommandHandler's doc comment).
        services.AddScoped<PlaceOrderCommandHandler>();
        services.AddScoped<PlaceOrderV2CommandHandler>();
        services.AddScoped<ConfirmOrderCommandHandler>();
        services.AddScoped<CancelOrderCommandHandler>();
        services.AddScoped<GetOrderByIdQueryHandler>();
        services.AddScoped<GetMyOrdersQueryHandler>();

        return services;
    }
}
