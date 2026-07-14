using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Application.Abstractions;

public interface IPaymentRepository
{
    Task<PaymentTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Used for idempotency dedup by OrderId (Commit 7) — at most 1 payment per order.</summary>
    Task<PaymentTransaction?> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default);

    void Add(PaymentTransaction payment);
}
