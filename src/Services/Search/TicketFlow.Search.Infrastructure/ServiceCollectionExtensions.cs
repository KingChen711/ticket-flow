using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Search.Application.Abstractions;

namespace TicketFlow.Search.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSearchInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var uri = configuration["Elasticsearch:Uri"]
            ?? throw new InvalidOperationException("Configuration 'Elasticsearch:Uri' was not found.");
        var indexName = configuration["Elasticsearch:IndexName"] ?? "events";

        var settings = new ElasticsearchClientSettings(new Uri(uri)).DefaultIndex(indexName);
        services.AddSingleton(new ElasticsearchClient(settings));

        services.AddSingleton<IEventSearchIndex>(sp =>
            new ElasticEventSearchIndex(sp.GetRequiredService<ElasticsearchClient>(), indexName));

        services.AddHealthChecks().AddCheck<ElasticsearchHealthCheck>("elasticsearch");

        return services;
    }
}
