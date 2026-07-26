using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using TicketFlow.Notification.Application;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        MongoSerializationConfig.EnsureConfigured();

        var connectionString = configuration["Mongo:ConnectionString"]
            ?? throw new InvalidOperationException("Configuration 'Mongo:ConnectionString' was not found.");
        var databaseName = configuration["Mongo:Database"] ?? "notification";

        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        services.AddSingleton<IEventReminderReadModel, MongoEventReminderReadModel>();
        services.AddSingleton<INotificationDeliveryStore, MongoNotificationDeliveryStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<NotificationDeliveryService>();

        var provider = ValidateDeliveryConfiguration(configuration, environment.EnvironmentName);
        if (string.Equals(provider, "Http", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = configuration["Delivery:BaseUrl"]!;
            var apiKey = configuration["Delivery:ApiKey"]!;
            services.AddHttpClient<INotificationProvider, HttpNotificationProvider>(client =>
            {
                client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
            });
        }
        else
        {
            services.AddSingleton<INotificationProvider, DevelopmentNotificationProvider>();
        }

        services.AddHealthChecks().AddCheck<MongoHealthCheck>(
            "mongodb", HealthStatus.Unhealthy, ["ready"], TimeSpan.FromSeconds(3));

        return services;
    }

    public static string ValidateDeliveryConfiguration(IConfiguration configuration, string environmentName)
    {
        var production = string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
        var stubAllowed = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        var provider = configuration["Delivery:Provider"];
        if (!stubAllowed && !string.Equals(provider, "Http", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment '{environmentName}' requires Delivery:Provider=Http; " +
                "the development stub is allowed only in Development or test.");
        }

        provider = string.IsNullOrWhiteSpace(provider) && stubAllowed ? "DevelopmentStub" : provider;
        if (string.Equals(provider, "Http", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(configuration["Delivery:BaseUrl"], UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Delivery:BaseUrl must be an absolute HTTP(S) URI.");
            if (production && uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Production Delivery:BaseUrl must use HTTPS.");
            if (string.IsNullOrWhiteSpace(configuration["Delivery:ApiKey"]))
                throw new InvalidOperationException("Delivery:ApiKey is required for the HTTP provider.");
            if (production &&
                (!Uri.TryCreate(configuration["Delivery:HealthUrl"], UriKind.Absolute, out var healthUri) ||
                 healthUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Production Delivery:HealthUrl must be an absolute HTTPS URI.");
            return "Http";
        }

        if (!string.Equals(provider, "DevelopmentStub", StringComparison.OrdinalIgnoreCase) ||
            production || !stubAllowed)
            throw new InvalidOperationException($"Unsupported notification delivery provider '{provider}'.");
        return "DevelopmentStub";
    }
}
