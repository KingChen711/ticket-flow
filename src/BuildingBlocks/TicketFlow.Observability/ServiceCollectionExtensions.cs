using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

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
    ///
    /// Logs (Commit 4): `Log.Logger` is a process-wide static, set once here at startup — same
    /// "one call, every service gets it" shape as the tracing/metrics setup above, just via
    /// Serilog's own static instead of DI. `Enrich.WithSpan()` alone (no message-template
    /// placeholder anywhere) is enough for the ECS formatter to map `TraceId`/`SpanId` onto the
    /// real `trace.id`/`span.id` fields — confirmed with a live probe against this stack's actual
    /// ES container before writing this, see Directory.Packages.props' Elastic.Serilog.Sinks
    /// comment for why the package is pinned to 8.12.3 and not the latest 9.0.0. One data stream
    /// per service (`logs-ticketflow-&lt;serviceName&gt;-local`) rather than one shared stream —
    /// mirrors Prometheus' per-service scrape-job granularity (mục 3), and Kibana's index pattern
    /// `logs-ticketflow-*` still spans all of them for the cross-service TraceId correlation view
    /// the DoD needs. `IlmPolicy` references a custom policy provisioned by the one-shot init
    /// container in docker-compose.monitoring.yml — the built-in "logs" ILM policy ES ships with
    /// only has a hot/rollover phase and no delete phase (confirmed live: infinite retention by
    /// default, and the policy itself is flagged `"deprecated": true` server-side), which is the
    /// opposite of the "retention tối thiểu" mục 4 calls for. Console sink stays alongside ES —
    /// `docker logs` keeps working exactly like every prior phase, no debug workflow lost.
    /// `AddLogging` (not `IHostBuilder.UseSerilog`) because this method is an `IServiceCollection`
    /// extension, not a host hook — confirmed via a live DI probe that `ILogger&lt;T&gt;` resolved
    /// this way still round-trips into both sinks with the enriched trace/span properties intact.
    /// </summary>
    public static IServiceCollection AddTicketFlowObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        var otlpEndpoint = configuration["Otlp:Endpoint"]
            ?? throw new InvalidOperationException("Configuration 'Otlp:Endpoint' was not found.");
        var elasticsearchUri = configuration["Elasticsearch:Uri"]
            ?? throw new InvalidOperationException("Configuration 'Elasticsearch:Uri' was not found.");

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithSpan()
            .WriteTo.Console()
            .WriteTo.Elasticsearch(new[] { new Uri(elasticsearchUri) }, opts =>
            {
                opts.DataStream = new DataStreamName("logs", $"ticketflow-{serviceName}", "local");
                // Silent, not Failure: Elasticsearch only stores logs, so a service must still start
                // (and keep logging to the console) when it is down. If it was down at startup, the
                // data stream template is not created and logs land under ES's built-in logs-* template.
                opts.BootstrapMethod = BootstrapMethod.Silent;
                opts.IlmPolicy = "ticketflow-logs-retention";
            })
            .CreateLogger();

        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(dispose: true);
        });

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
