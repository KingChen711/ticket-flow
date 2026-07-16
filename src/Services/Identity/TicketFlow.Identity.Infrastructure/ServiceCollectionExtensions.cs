using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Infrastructure.Keycloak;

namespace TicketFlow.Identity.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddDbContext<IdentityDbContext>(options =>
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        var keycloakServerUrl = configuration["Keycloak:ServerUrl"]
            ?? throw new InvalidOperationException("Configuration 'Keycloak:ServerUrl' was not found.");
        var adminOptions = new KeycloakAdminOptions(
            Realm: configuration["Keycloak:Realm"] ?? throw new InvalidOperationException("Configuration 'Keycloak:Realm' was not found."),
            AdminClientId: configuration["Keycloak:AdminClientId"] ?? throw new InvalidOperationException("Configuration 'Keycloak:AdminClientId' was not found."),
            AdminClientSecret: configuration["Keycloak:AdminClientSecret"] ?? throw new InvalidOperationException("Configuration 'Keycloak:AdminClientSecret' was not found."));

        services.AddSingleton(adminOptions);
        services.AddHttpClient<IKeycloakAdminClient, KeycloakAdminClient>(client =>
        {
            client.BaseAddress = new Uri(keycloakServerUrl);
        });

        return services;
    }
}
