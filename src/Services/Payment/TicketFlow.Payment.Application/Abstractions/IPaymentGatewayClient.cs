namespace TicketFlow.Payment.Application.Abstractions;

/// <summary>
/// Port for "the outside world" — Application knows it needs to charge a payment method and get
/// back success/failure, not HOW (fake gateway now, Stripe/VNPay later — see Phase 5 backlog).
/// Infrastructure's implementation is where Polly (retry/circuit-breaker/timeout) wraps the
/// actual call — Application stays resiliency-policy-agnostic.
/// </summary>
public interface IPaymentGatewayClient
{
    Task<GatewayChargeResult> ChargeAsync(
        Guid orderId,
        decimal amount,
        string paymentMethod,
        CancellationToken cancellationToken = default);
}

public sealed record GatewayChargeResult(bool Succeeded, string? GatewayRef, string? FailureReason);
