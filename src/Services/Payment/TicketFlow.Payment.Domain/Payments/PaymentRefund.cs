using TicketFlow.Domain;

namespace TicketFlow.Payment.Domain.Payments;

/// <summary>Reserved schema for gateway refund attempts; activation belongs to the v2 workflow.</summary>
public sealed class PaymentRefund : Entity<Guid>
{
    public Guid PaymentId { get; private set; }
    public Guid OrderId { get; private set; }
    public string OperationKey { get; private set; } = null!;
    public PaymentRefundState State { get; private set; }
    public string? GatewayReference { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private PaymentRefund() { }
}
