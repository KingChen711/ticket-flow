using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TicketFlow.Observability;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Trace: sync hops via ASP.NET Core/HttpClient/GrpcNetClient instrumentation (Commit 1),
    /// async hops via `.AddSource("MassTransit")` (Commit 2) — see those commits' doc history in
    /// docs/phase-10-plan.md for the live-probe findings behind each. OTLP straight to Jaeger
    /// all-in-one (gRPC 4317) — no Collector in between, see mục 2 for why a single-node local
    /// stack doesn't earn one.
    ///
    /// Metrics (Commit 3): `AddAspNetCoreInstrumentation()` alone gives RED for free —
    /// `http.server.request.duration` is a histogram tagged with route/method/status_code, which
    /// is Rate+Errors+Duration in one instrument, no custom code needed. `configureMetrics` is
    /// the one escape hatch a caller needs beyond that — Ordering uses it to register its own
    /// `ticketflow.tickets.sold` Meter (mục 3) via `.AddMeter("TicketFlow.Ordering")`; every other
    /// service passes nothing. Exporter is Prometheus (pull, not OTLP push) — mục 3's reasoning:
    /// pull is idiomatic Prometheus, and this repo already accepted one no-stable-release OTel
    /// package for gRPC tracing (Commit 1), so a second one for the exporter isn't a new kind of
    /// risk, see Directory.Packages.props.
    /// </summary>
    public static IServiceCollection AddTicketFlowObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<MeterProviderBuilder>? configureMetrics = null)
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
                .AddSource("MassTransit")
                .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                configureMetrics?.Invoke(metrics);
                metrics.AddPrometheusExporter();
            });

        return services;
    }
}
