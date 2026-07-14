using Polly.CircuitBreaker;

namespace TicketFlow.Payment.Infrastructure.Tests;

// Exercises the exact pipeline configuration used in production (retry + circuit breaker +
// timeout) with a fully controllable fake delegate — not through FakePaymentGatewayClient's
// fixed .13-fails-always hook, which can't cleanly distinguish "retries exhausted" from
// "circuit breaker opened" since ChargeAsync catches everything into the same failure shape.
public class PaymentGatewayResiliencePipelineTests
{
    [Fact]
    public async Task ExecuteAsync_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        var pipeline = PaymentGatewayResiliencePipeline.Create();
        var attempts = 0;

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("transient gateway hiccup");

            await Task.CompletedTask;
            return "ok";
        });

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_AlwaysFailing_RetriesThenPropagatesException()
    {
        var pipeline = PaymentGatewayResiliencePipeline.Create();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            pipeline.ExecuteAsync<string>(async _ =>
            {
                attempts++;
                await Task.CompletedTask;
                throw new InvalidOperationException("gateway declined");
            }).AsTask());

        // 1 initial attempt + MaxRetryAttempts=3 retries = 4 attempts for this single call
        // (unless the circuit breaker trips mid-call on a fresh pipeline it won't, since this
        // is the very first call).
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedFailures_EventuallyOpensCircuitBreaker()
    {
        var pipeline = PaymentGatewayResiliencePipeline.Create();

        async Task CallAlwaysFailingGatewayAsync() =>
            await pipeline.ExecuteAsync<string>(async _ =>
            {
                await Task.CompletedTask;
                throw new InvalidOperationException("gateway down");
            });

        var sawBrokenCircuit = false;
        for (var i = 0; i < 8 && !sawBrokenCircuit; i++)
        {
            try
            {
                await CallAlwaysFailingGatewayAsync();
            }
            catch (BrokenCircuitException)
            {
                sawBrokenCircuit = true;
            }
            catch (Exception)
            {
                // Retries-exhausted failure from before the circuit tripped — expected, keep
                // driving failures until MinimumThroughput/FailureRatio trips the breaker.
            }
        }

        Assert.True(sawBrokenCircuit, "Expected the circuit breaker to open (BrokenCircuitException) after repeated gateway failures.");
    }
}
