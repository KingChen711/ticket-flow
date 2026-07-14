using TicketFlow.Domain;

namespace TicketFlow.Payment.Domain.Payments;

/// <summary>
/// Named PaymentTransaction, not Payment — a real C# ambiguity caught while writing the domain
/// test project: this project's namespace root IS "TicketFlow.Payment" (the service name), so a
/// type literally named "Payment" collides with that namespace segment (CS0234 "Initiate does
/// not exist in namespace TicketFlow.Payment" when referenced from a sibling namespace). Unlike
/// Order≠Ordering or Event≠Catalog elsewhere in this repo, "Payment" the concept and "Payment"
/// the service are the same word — renaming the aggregate side-steps it, matching the same
/// "Order lives inside Ordering" naming pattern already used for Phase 4.
///
/// State-based aggregate (EF Core, Phase 5 mục 9 — payment/ticketing are NOT event-sourced,
/// only ordering is). Extends Entity&lt;Guid&gt;, not AggregateRoot&lt;Guid&gt; — no domain
/// events are raised here: the integration events consumers publish after a successful
/// transition (PaymentProcessed/PaymentFailed/PaymentRefunded, Contracts.Messaging) are built
/// directly from the call result, not translated from a buffered domain event via Outbox — so
/// AggregateRoot's Raise()/DomainEvents machinery would go entirely unused (same reasoning as
/// Order/Seat in earlier phases).
/// </summary>
public sealed class PaymentTransaction : Entity<Guid>
{
    public Guid OrderId { get; private set; }
    public Money Amount { get; private set; } = null!;
    public PaymentStatus Status { get; private set; }
    public string? GatewayRef { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset InitiatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }

    // Required by EF Core.
    private PaymentTransaction() { }

    private PaymentTransaction(Guid id, Guid orderId, Money amount, DateTimeOffset now)
        : base(id)
    {
        OrderId = orderId;
        Amount = amount;
        Status = PaymentStatus.Pending;
        InitiatedAt = now;
    }

    public static PaymentTransaction Initiate(Guid orderId, Money amount, DateTimeOffset now) =>
        new(Guid.NewGuid(), orderId, amount ?? throw new ArgumentNullException(nameof(amount)), now);

    public void MarkSucceeded(string gatewayRef, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException($"Only a pending payment can succeed (current status: {Status}).");

        Status = PaymentStatus.Succeeded;
        GatewayRef = Guard.NotNullOrEmpty(gatewayRef, nameof(gatewayRef));
        ProcessedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException($"Only a pending payment can fail (current status: {Status}).");

        Status = PaymentStatus.Failed;
        FailureReason = Guard.NotNullOrEmpty(reason, nameof(reason));
        ProcessedAt = now;
    }

    public void MarkRefunded(DateTimeOffset now)
    {
        if (Status != PaymentStatus.Succeeded)
            throw new InvalidOperationException($"Only a succeeded payment can be refunded (current status: {Status}).");

        Status = PaymentStatus.Refunded;
        RefundedAt = now;
    }
}
