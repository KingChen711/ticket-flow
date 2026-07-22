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
                // Phase 12 Commit 6: a declined card is the provider's answer, not a fault.
                // Retrying it wastes three more round trips on a guaranteed refusal, and — worse
                // — feeds the circuit breaker below, so enough legitimately declined cards would
                // open the circuit for everyone else.
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(10),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
            })
            .AddTimeout(TimeSpan.FromSeconds(5))
            .Build();

    /// <summary>
    /// Everything except an explicit provider refusal is treated as transient. Being wrong in
    /// this direction costs a retry; being wrong the other way (treating a timeout as a decline)
    /// cancels an order that may already be paid.
    /// </summary>
    private static bool IsTransient(Exception exception) => exception is not GatewayDeclinedException;
}
