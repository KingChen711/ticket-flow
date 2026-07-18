using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Telemetry;

namespace TicketFlow.Payment.Infrastructure;

/// <summary>
/// Extracted from ServiceCollectionExtensions into its own factory so tests can exercise the
/// exact same pipeline configuration directly (with a controllable fake delegate) instead of
/// only observing it indirectly through FakePaymentGatewayClient's fixed .13-fails-always hook —
/// that hook proves the deterministic-failure design works, but can't cleanly distinguish
/// "retries exhausted" from "circuit breaker opened" from the outside, since ChargeAsync
/// catches everything into the same GatewayChargeResult(false, ...) shape.
///
/// `.ConfigureTelemetry(new TelemetryOptions())` (Commit 5, mục 3 rủi ro số 3) — the metrics-only
/// overload, no `ILoggerFactory` — confirmed via a live probe that Polly.Core alone publishes no
/// metric at all; this is what turns on the `Polly` Meter (`resilience.polly.strategy.events`,
/// tagged `event.name`: `OnCircuitOpened`/`OnRetry`/etc.), the source both Grafana alert rules in
/// docs/phase-10-plan.md Commit 5 read from. Passing an `ILoggerFactory` instead would ALSO work
/// but doubles up a second, noisier logging stream on top of Serilog's own Payment logs
/// (Commit 4) for events already visible as metrics — confirmed both overloads emit identical
/// measurements, so the logger-less one is strictly less noise for the same signal.
/// </summary>
public static class PaymentGatewayResiliencePipeline
{
    public static ResiliencePipeline Create() =>
        new ResiliencePipelineBuilder()
            .ConfigureTelemetry(new TelemetryOptions())
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(15),
            })
            .AddTimeout(TimeSpan.FromSeconds(5))
            .Build();
}
