using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TicketFlow.Web;

public static class ServiceCollectionExtensions
{
    public const string ReadinessTag = "ready";

    public static IServiceCollection AddTicketFlowWeb(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }

    /// <summary>
    /// Adds a bounded datastore check to readiness. A fresh scope is required because DbContext
    /// is scoped while health-check registrations are singleton. Liveness deliberately excludes
    /// this check: a dependency outage must remove the pod from service without restarting every
    /// replica and amplifying the outage.
    /// </summary>
    public static IServiceCollection AddDbContextReadinessCheck<TDbContext>(
        this IServiceCollection services,
        string name,
        TimeSpan? timeout = null)
        where TDbContext : DbContext
    {
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            name,
            serviceProvider => new DbContextReadinessHealthCheck<TDbContext>(
                serviceProvider.GetRequiredService<IServiceScopeFactory>()),
            HealthStatus.Unhealthy,
            [ReadinessTag],
            timeout ?? TimeSpan.FromSeconds(3)));
        return services;
    }

    /// <summary>Adds a bounded HTTP dependency check to readiness.</summary>
    public static IServiceCollection AddHttpReadinessCheck(
        this IServiceCollection services,
        string name,
        Uri endpoint,
        TimeSpan? timeout = null)
    {
        var clientName = $"ticketflow-health-{name}";
        services.AddHttpClient(clientName, client =>
        {
            client.Timeout = timeout ?? TimeSpan.FromSeconds(3);
        });
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            name,
            serviceProvider => new HttpReadinessHealthCheck(
                serviceProvider.GetRequiredService<IHttpClientFactory>(), clientName, endpoint),
            HealthStatus.Unhealthy,
            [ReadinessTag],
            timeout ?? TimeSpan.FromSeconds(3)));
        return services;
    }

    public static WebApplication UseTicketFlowWeb(this WebApplication app)
    {
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        // Only services that called AddTicketFlowAuth register IAuthenticationSchemeProvider —
        // UseAuthorization() throws at startup without a matching AddAuthorization() call, so
        // this stays opt-in per service instead of running unconditionally for everyone.
        if (app.Services.GetService<IAuthenticationSchemeProvider>() is not null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        var ready = new HealthCheckOptions
        {
            // MassTransit registers its transport checks without a stable tag contract across
            // the pinned 8.x line. Include every dependency check here; only /live filters them.
            Predicate = _ => true,
        };
        var live = new HealthCheckOptions { Predicate = _ => false };

        app.MapHealthChecks("/health/live", live);
        app.MapHealthChecks("/health/ready", ready);
        // Compatibility alias for Phase 1-16 callers. Kubernetes uses the two explicit routes.
        app.MapHealthChecks("/health", ready);
        return app;
    }

    private sealed class DbContextReadinessHealthCheck<TDbContext>(IServiceScopeFactory scopeFactory)
        : IHealthCheck
        where TDbContext : DbContext
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
                return await dbContext.Database.CanConnectAsync(cancellationToken)
                    ? HealthCheckResult.Healthy("Database connection succeeded.")
                    : HealthCheckResult.Unhealthy("Database connection failed.");
            }
            catch (Exception exception)
            {
                return HealthCheckResult.Unhealthy("Database connection failed.", exception);
            }
        }
    }

    private sealed class HttpReadinessHealthCheck(
        IHttpClientFactory clientFactory,
        string clientName,
        Uri endpoint) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await clientFactory.CreateClient(clientName)
                    .GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                return response.IsSuccessStatusCode
                    ? HealthCheckResult.Healthy($"Dependency '{endpoint}' is ready.")
                    : HealthCheckResult.Unhealthy(
                        $"Dependency '{endpoint}' returned HTTP {(int)response.StatusCode}.");
            }
            catch (Exception exception)
            {
                return HealthCheckResult.Unhealthy($"Dependency '{endpoint}' is unavailable.", exception);
            }
        }
    }
}
