namespace TicketFlow.Payment.Application.Abstractions;

/// <summary>
/// What the provider actually told us. The pre-Phase-12 port returned a bare
/// <c>bool Succeeded</c>, which forced every timeout, open circuit and lost response to be
/// recorded as a decline — the single most expensive misreading available, because it releases
/// the seats and cancels an order that may in fact be paid.
/// </summary>
public enum GatewayChargeOutcome
{
    /// <summary>The provider confirmed the charge.</summary>
    Authorized,

    /// <summary>The provider explicitly refused. Safe to compensate.</summary>
    Declined,

    /// <summary>No usable answer. The charge may or may not exist; only a lookup can decide.</summary>
    Unknown,
}

public sealed record GatewayChargeRequest(
    Guid OrderId,
    /// <summary>Stable per-order key. The provider must collapse repeats of the same key into one
    /// charge — this is what makes a retry safe rather than a second debit.</summary>
    string IdempotencyKey,
    decimal Amount,
    string Currency,
    string PaymentMethod);

public sealed record GatewayChargeResult(
    GatewayChargeOutcome Outcome,
    string? GatewayReference,
    string? FailureReason)
{
    public static GatewayChargeResult Authorized(string gatewayReference) =>
        new(GatewayChargeOutcome.Authorized, gatewayReference, null);

    public static GatewayChargeResult Declined(string reason) =>
        new(GatewayChargeOutcome.Declined, null, reason);

    public static GatewayChargeResult Unknown(string reason) =>
        new(GatewayChargeOutcome.Unknown, null, reason);
}

public sealed record GatewayRefundRequest(
    Guid OrderId,
    string IdempotencyKey,
    string ChargeReference,
    decimal Amount,
    string Currency);

/// <summary>
/// Port for "the outside world". Phase 12 Commit 6 added two things the previous shape could not
/// express: an idempotency key on every mutating call, and a lookup so an ambiguous outcome can
/// be resolved by asking the provider what really happened instead of guessing.
/// </summary>
public interface IPaymentGatewayClient
{
    Task<GatewayChargeResult> ChargeAsync(
        GatewayChargeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask the provider about a previously sent idempotency key. Returns <c>null</c> when the
    /// provider has no record of it — which, for a key we definitely sent, is itself meaningful:
    /// the charge never landed.
    /// </summary>
    Task<GatewayChargeResult?> LookupChargeAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<GatewayChargeResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken = default);

    Task<GatewayChargeResult?> LookupRefundAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
