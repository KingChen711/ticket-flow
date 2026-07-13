using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Reservation.Application.Seats;

namespace TicketFlow.Reservation.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReservationApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new ReservationOptions());
        services.AddScoped<InventoryService>();
        services.AddScoped<ReservationService>();

        return services;
    }
}
