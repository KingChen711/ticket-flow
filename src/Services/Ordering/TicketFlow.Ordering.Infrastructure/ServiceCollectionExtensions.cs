using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddMarten(options =>
        {
            options.Connection(connectionString);
            // Dev-only, same spirit as EF's migrate-on-startup elsewhere in this repo — a real
            // deployment would run schema setup as a separate CI/CD step instead.
            options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
        });

        services.AddScoped<IOrderStore, MartenOrderStore>();

        services.AddHealthChecks().AddCheck<MartenHealthCheck>("marten");

        return services;
    }
}
