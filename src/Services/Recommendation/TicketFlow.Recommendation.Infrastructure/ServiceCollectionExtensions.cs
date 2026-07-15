using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using TicketFlow.Recommendation.Application.Abstractions;
using TicketFlow.Recommendation.Application.Recommendations;

namespace TicketFlow.Recommendation.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRecommendationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        MongoSerializationConfig.EnsureConfigured();

        var connectionString = configuration["Mongo:ConnectionString"]
            ?? throw new InvalidOperationException("Configuration 'Mongo:ConnectionString' was not found.");
        var databaseName = configuration["Mongo:Database"] ?? "recommendation";

        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        services.AddSingleton<IPurchaseHistoryStore, MongoPurchaseHistoryStore>();

        var searchBaseUrl = configuration["Search:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration 'Search:BaseUrl' was not found.");
        services.AddHttpClient<ISearchClient, HttpSearchClient>(client => client.BaseAddress = new Uri(searchBaseUrl));

        services.AddScoped<GetRecommendationsQueryHandler>();

        services.AddHealthChecks().AddCheck<MongoHealthCheck>("mongodb");

        return services;
    }
}
