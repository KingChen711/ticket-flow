using Amazon.Lambda;
using Amazon.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Application.Tickets;

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
        services.AddScoped<ITicketIssuanceJobRepository, TicketIssuanceJobRepository>();
        services.AddScoped<IEventOwnerRepository, EventOwnerRepository>();
        services.AddScoped<IGateAssignmentRepository, GateAssignmentRepository>();
        services.AddScoped<GateAccessPolicy>();
        services.AddScoped<TicketAccessPolicy>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ITicketPdfRenderer, LambdaTicketRenderClient>();

        // Commit 3 — same IAmazonLambda-pointed-at-LocalStack pattern as Gateway's Program.cs
        // (Commit 2): "test"/"test" credentials because the SDK requires non-empty values to
        // sign requests even though LocalStack doesn't validate them.
        var lambdaServiceUrl = configuration["Aws:LambdaServiceUrl"];
        var production = string.Equals(
            configuration["ASPNETCORE_ENVIRONMENT"], "Production", StringComparison.OrdinalIgnoreCase);
        services.AddSingleton<IAmazonLambda>(_ => CreateLambdaClient(configuration, lambdaServiceUrl, production));

        var minioEndpoint = configuration["Minio:Endpoint"]
            ?? throw new InvalidOperationException("Configuration 'Minio:Endpoint' was not found.");
        var minioAccessKey = configuration["Minio:AccessKey"]
            ?? throw new InvalidOperationException("Configuration 'Minio:AccessKey' was not found.");
        var minioSecretKey = configuration["Minio:SecretKey"]
            ?? throw new InvalidOperationException("Configuration 'Minio:SecretKey' was not found.");
        var bucketName = configuration["Minio:BucketName"] ?? "tickets";

        var useMinioTls = bool.TryParse(configuration["Minio:UseSsl"], out var configuredMinioTls) &&
                          configuredMinioTls;
        if (production && !useMinioTls)
            throw new InvalidOperationException("Production requires Minio:UseSsl=true.");

        // SDK-provided DI extension (Minio.ServiceCollectionExtensions) — registers IMinioClient
        // as a singleton, built via the same WithEndpoint/WithCredentials/WithSSL/Build chain.
        services.AddMinio(client => client
            .WithEndpoint(minioEndpoint)
            .WithCredentials(minioAccessKey, minioSecretKey)
            .WithSSL(useMinioTls)
            .Build());

        services.AddScoped<ITicketObjectStorage>(sp =>
            new MinioTicketObjectStorage(sp.GetRequiredService<IMinioClient>(), bucketName));

        return services;
    }

    private static IAmazonLambda CreateLambdaClient(
        IConfiguration configuration,
        string? serviceUrl,
        bool production)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new AmazonLambdaClient();

        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var endpoint) ||
            (production && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                "Aws:LambdaServiceUrl must be an absolute URI and must use HTTPS in Production.");

        var accessKey = configuration["Aws:AccessKey"];
        var secretKey = configuration["Aws:SecretKey"];
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            if (production)
                throw new InvalidOperationException("An external Lambda emulator requires secret-backed AWS credentials.");
            accessKey = "test";
            secretKey = "test";
        }

        return new AmazonLambdaClient(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonLambdaConfig { ServiceURL = endpoint.AbsoluteUri });
    }
}
