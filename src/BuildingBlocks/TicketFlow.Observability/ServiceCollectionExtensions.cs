using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TicketFlow.Observability;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Phase 10 Commit 1 — trace only, sync hops (HTTP via ASP.NET Core/HttpClient
    /// instrumentation, gRPC via GrpcNetClient instrumentation for ordering→reservation). OTLP
    /// straight to Jaeger all-in-one (gRPC 4317) — no Collector in between, see
    /// docs/phase-10-plan.md mục 2 for why a single-node local stack doesn't earn one.
    ///
    /// `.AddSource("MassTransit")` — needed to see saga hops (RabbitMQ/Kafka Rider) in the same
    /// trace — is deliberately NOT here yet; a live probe against real RabbitMQ (docs/phase-10-plan.md
    /// Commit 1 write-up) confirmed MassTransit 8.5.10 already propagates W3C trace context
    /// end-to-end on its own (custom header `MT-Activity-Id`, W3C traceparent format, read
    /// automatically on the consuming side — no manual header plumbing needed), so wiring that
    /// source is a 1-line addition saved for Commit 2 rather than pulled in speculatively here.
    /// Metrics (`.WithMetrics`) are Commit 3's job (Prometheus), not this method's.
    /// </summary>
    public static IServiceCollection AddTicketFlowObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        var otlpEndpoint = configuration["Otlp:Endpoint"]
            ?? throw new InvalidOperationException("Configuration 'Otlp:Endpoint' was not found.");

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // No stable release exists for this package (confirmed via `dotnet add package`
                // refusing to resolve a stable version) — accepted as the only real option for
                // the ordering→reservation gRPC client span, see Directory.Packages.props comment.
                .AddGrpcClientInstrumentation()
                .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint)));

        return services;
    }
}
