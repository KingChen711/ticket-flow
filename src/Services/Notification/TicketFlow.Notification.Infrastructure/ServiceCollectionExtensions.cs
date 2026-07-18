using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using TicketFlow.Notification.Application.Abstractions;

namespace TicketFlow.Notification.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        MongoSerializationConfig.EnsureConfigured();

        var connectionString = configuration["Mongo:ConnectionString"]
            ?? throw new InvalidOperationException("Configuration 'Mongo:ConnectionString' was not found.");
        var databaseName = configuration["Mongo:Database"] ?? "notification";

        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        services.AddSingleton<IEventReminderReadModel, MongoEventReminderReadModel>();
        services.AddHealthChecks().AddCheck<MongoHealthCheck>("mongodb");

        return services;
    }
}
