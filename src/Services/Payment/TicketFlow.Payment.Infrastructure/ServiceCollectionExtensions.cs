using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Payment.Application.Abstractions;

namespace TicketFlow.Payment.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddDbContext<PaymentDbContext>(options =>
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Singleton — a ResiliencePipeline is immutable and thread-safe once built; building it
        // once at startup avoids reconstructing the retry/circuit-breaker/timeout chain on every
        // charge attempt. Retry: 3 attempts, exponential backoff. Circuit breaker: opens once
        // >=50% of the last window of >=4 calls fail, stays open 15s (fail fast instead of
        // hammering an already-struggling gateway). Timeout: 5s per attempt.
        services.AddSingleton(PaymentGatewayResiliencePipeline.Create());
        services.AddScoped<IPaymentGatewayClient, FakePaymentGatewayClient>();

        return services;
    }
}
