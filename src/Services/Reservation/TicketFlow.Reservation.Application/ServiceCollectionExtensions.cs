using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Reservation.Application.Seats;

namespace TicketFlow.Reservation.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReservationApplication(
        this IServiceCollection services,
        ReservationOptions? options = null)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(options ?? new ReservationOptions());
        services.AddScoped<InventoryService>();
        services.AddScoped<ReservationService>();

        return services;
    }
}
