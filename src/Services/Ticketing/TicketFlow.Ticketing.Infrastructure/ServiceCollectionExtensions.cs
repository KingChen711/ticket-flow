using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTicketingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddDbContext<TicketingDbContext>(options =>
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

        services.AddScoped<ITicketRepository, TicketRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ITicketPdfRenderer, QuestPdfTicketRenderer>();

        var minioEndpoint = configuration["Minio:Endpoint"]
            ?? throw new InvalidOperationException("Configuration 'Minio:Endpoint' was not found.");
        var minioAccessKey = configuration["Minio:AccessKey"]
            ?? throw new InvalidOperationException("Configuration 'Minio:AccessKey' was not found.");
        var minioSecretKey = configuration["Minio:SecretKey"]
            ?? throw new InvalidOperationException("Configuration 'Minio:SecretKey' was not found.");
        var bucketName = configuration["Minio:BucketName"] ?? "tickets";

        // SDK-provided DI extension (Minio.ServiceCollectionExtensions) — registers IMinioClient
        // as a singleton, built via the same WithEndpoint/WithCredentials/WithSSL/Build fluent
        // chain the SDK documents. No TLS — matches the plain-HTTP MinIO container (Commit 1).
        services.AddMinio(client => client
            .WithEndpoint(minioEndpoint)
            .WithCredentials(minioAccessKey, minioSecretKey)
            .WithSSL(false)
            .Build());

        services.AddScoped<ITicketObjectStorage>(sp =>
            new MinioTicketObjectStorage(sp.GetRequiredService<IMinioClient>(), bucketName));

        return services;
    }
}
