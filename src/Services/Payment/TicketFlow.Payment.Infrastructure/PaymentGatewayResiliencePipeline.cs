using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace TicketFlow.Payment.Infrastructure;

/// <summary>
/// Extracted from ServiceCollectionExtensions into its own factory so tests can exercise the
/// exact same pipeline configuration directly (with a controllable fake delegate) instead of
/// only observing it indirectly through FakePaymentGatewayClient's fixed .13-fails-always hook —
/// that hook proves the deterministic-failure design works, but can't cleanly distinguish
/// "retries exhausted" from "circuit breaker opened" from the outside, since ChargeAsync
/// catches everything into the same GatewayChargeResult(false, ...) shape.
/// </summary>
public static class PaymentGatewayResiliencePipeline
{
    public static ResiliencePipeline Create() =>
        new ResiliencePipelineBuilder()
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
