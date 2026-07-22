using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TicketFlow.Payment.Application.Abstractions;

namespace TicketFlow.Payment.Infrastructure;

/// <summary>
/// Simulates an external, not-fully-reliable payment provider. Two Phase 12 Commit 6 behaviours
/// matter more than the simulation itself:
///
/// 1. <b>Idempotency by key.</b> A repeat of the same <c>IdempotencyKey</c> returns the original
///    charge instead of creating a second one — which is what makes retrying a lost response safe
///    rather than a double debit. A real provider (Stripe, VNPay) offers exactly this; the
///    in-memory ledger below stands in for it.
/// 2. <b>Honest failure classification.</b> A declined card is a decision; a timeout or an open
///    circuit is an absence of one. The previous implementation caught every exception and
///    returned "not succeeded", which made the two indistinguishable and let a transport blip
///    cancel a paid order.
/// </summary>
public sealed class FakePaymentGatewayClient(
    ResiliencePipeline pipeline,
    ILogger<FakePaymentGatewayClient> logger)
    : IPaymentGatewayClient
{
    /// <summary>Stands in for the provider's own idempotency ledger. Static so it survives the
    /// scoped client lifetime the way the real provider's records survive our process.</summary>
    private static readonly ConcurrentDictionary<string, GatewayChargeResult> Ledger = new();

    public async Task<GatewayChargeResult> ChargeAsync(
        GatewayChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Ledger.TryGetValue(request.IdempotencyKey, out var existing))
        {
            logger.LogInformation(
                "[payment] provider replayed idempotency key {IdempotencyKey} for order {OrderId}; no second charge.",
                request.IdempotencyKey,
                request.OrderId);
            return existing;
        }

        try
        {
            var result = await pipeline.ExecuteAsync(
                async ct =>
                {
                    await SimulateGatewayCallAsync(request.Amount, ct);
                    return GatewayChargeResult.Authorized($"gw-{Guid.NewGuid():N}");
                },
                cancellationToken);

            Ledger[request.IdempotencyKey] = result;
            return result;
        }
        catch (GatewayDeclinedException declined)
        {
            // A real decision from the provider: safe to compensate on.
            var result = GatewayChargeResult.Declined(declined.Message);
            Ledger[request.IdempotencyKey] = result;
            return result;
        }
        catch (Exception exception) when (exception is TimeoutRejectedException or BrokenCircuitException)
        {
            logger.LogWarning(
                exception,
                "[payment] charge for order {OrderId} produced no provider answer; recording Unknown, not a decline.",
                request.OrderId);
            return GatewayChargeResult.Unknown(exception.Message);
        }
    }

    public Task<GatewayChargeResult?> LookupChargeAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Ledger.GetValueOrDefault(idempotencyKey));

    public async Task<GatewayChargeResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Ledger.TryGetValue(request.IdempotencyKey, out var existing))
            return existing;

        try
        {
            var result = await pipeline.ExecuteAsync(
                async ct =>
                {
                    await Task.Delay(20, ct);
                    return GatewayChargeResult.Authorized($"rf-{Guid.NewGuid():N}");
                },
                cancellationToken);

            Ledger[request.IdempotencyKey] = result;
            return result;
        }
        catch (Exception exception) when (exception is TimeoutRejectedException or BrokenCircuitException)
        {
            logger.LogWarning(
                exception,
                "[payment] refund for order {OrderId} produced no provider answer; recording Unknown.",
                request.OrderId);
            return GatewayChargeResult.Unknown(exception.Message);
        }
    }

    public Task<GatewayChargeResult?> LookupRefundAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Ledger.GetValueOrDefault(idempotencyKey));

    /// <summary>
    /// Deterministic failure hook (Phase 5 plan mục 5) — an amount ending in .13 always declines,
    /// simulating a rejected card without random flakiness. Throws a dedicated exception type so
    /// the caller can tell this real decision apart from a transport failure.
    /// </summary>
    private static async Task SimulateGatewayCallAsync(decimal amount, CancellationToken cancellationToken)
    {
        await Task.Delay(50, cancellationToken);

        if (amount % 1.00m == 0.13m)
            throw new GatewayDeclinedException("Gateway declined the charge.");
    }
}

/// <summary>An explicit refusal by the provider, as opposed to no answer at all.</summary>
public sealed class GatewayDeclinedException(string message) : Exception(message);
