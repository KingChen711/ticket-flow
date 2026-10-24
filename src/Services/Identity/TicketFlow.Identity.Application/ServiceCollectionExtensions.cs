using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Identity.Application.Users;

namespace TicketFlow.Identity.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new RegistrationWorkflowOptions());
        services.AddScoped<UserService>();

        return services;
    }
}
