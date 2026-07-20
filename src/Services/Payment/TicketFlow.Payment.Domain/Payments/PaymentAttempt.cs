using TicketFlow.Domain;

namespace TicketFlow.Payment.Domain.Payments;

/// <summary>Reserved schema for gateway-attempt idempotency; behavior is intentionally deferred.</summary>
public sealed class PaymentAttempt : Entity<Guid>
{
    public Guid OrderId { get; private set; }
    public string OperationKey { get; private set; } = null!;
    public PaymentAttemptState State { get; private set; }
    public string? GatewayReference { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private PaymentAttempt() { }
}
