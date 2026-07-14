using Microsoft.Extensions.Logging;
using Polly;
using TicketFlow.Payment.Application.Abstractions;

namespace TicketFlow.Payment.Infrastructure;

/// <summary>
/// Simulates calling an external, not-fully-reliable payment gateway. The Polly
/// ResiliencePipeline (registered in ServiceCollectionExtensions — retry + circuit breaker +
/// timeout) wraps SimulateGatewayCallAsync, exactly at the boundary Ch10 is about: an untrusted
/// call to a system we don't control. Nowhere else in this service is wrapped in Polly.
/// </summary>
public sealed class FakePaymentGatewayClient(ResiliencePipeline pipeline, ILogger<FakePaymentGatewayClient> logger)
    : IPaymentGatewayClient
{
    public async Task<GatewayChargeResult> ChargeAsync(
        Guid orderId,
        decimal amount,
        string paymentMethod,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var gatewayRef = await pipeline.ExecuteAsync(
                async ct =>
                {
                    await SimulateGatewayCallAsync(amount, ct);
                    return $"gw-{Guid.NewGuid():N}";
                },
                cancellationToken);

            return new GatewayChargeResult(true, gatewayRef, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Payment gateway charge failed for order {OrderId} after resilience pipeline exhausted", orderId);
            return new GatewayChargeResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Deterministic failure hook (Phase 5 plan mục 5) — an amount ending in .13 always "fails"
    /// at the gateway, simulating a declined card without relying on random flakiness. Lets
    /// tests/DoD scenarios trigger the payment-fail compensation branch reliably.
    /// </summary>
    private static async Task SimulateGatewayCallAsync(decimal amount, CancellationToken cancellationToken)
    {
        await Task.Delay(50, cancellationToken);

        if (amount % 1.00m == 0.13m)
            throw new InvalidOperationException("Gateway declined the charge.");
    }
}
