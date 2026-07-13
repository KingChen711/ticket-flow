using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Identity.Application.Users;

namespace TicketFlow.Identity.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        services.AddScoped<UserService>();

        return services;
    }
}
