using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Application.Abstractions;

public interface IPaymentRepository
{
    Task<PaymentTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Used for idempotency dedup by OrderId (Commit 7) — at most 1 payment per order.</summary>
    Task<PaymentTransaction?> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default);

    void Add(PaymentTransaction payment);
}

/// <summary>
/// Charge/refund attempt storage. Separate from <see cref="IPaymentRepository"/> because the
/// attempt is claimed and committed on its own, before the provider call — it is not part of the
/// same unit of work as whatever the caller does afterwards.
/// </summary>
public interface IPaymentAttemptRepository
{
    /// <summary>
    /// Serializes everything touching one order. Combined with the unique OrderId index this
    /// gives both a fast path (lock) and a correctness backstop (constraint) against two
    /// consumers charging the same order.
    /// </summary>
    Task<IAsyncDisposable> AcquireOrderLockAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsyncDisposable>(NoopPaymentLock.Instance);

    Task<PaymentAttempt?> GetAttemptByOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<PaymentAttempt?> GetAttemptByIdAsync(Guid attemptId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentAttempt>> GetUnresolvedAttemptsAsync(
        int maxCount,
        CancellationToken cancellationToken = default);

    void AddAttempt(PaymentAttempt attempt);

    Task<PaymentRefund?> GetRefundByOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentRefund>> GetUnresolvedRefundsAsync(
        int maxCount,
        CancellationToken cancellationToken = default);

    void AddRefund(PaymentRefund refund);

    /// <summary>True when the exception is a unique-constraint violation, i.e. a concurrent
    /// claimer won the race. Lives here so Application stays free of provider types.</summary>
    bool IsUniqueViolation(Exception exception);
}

public sealed class NoopPaymentLock : IAsyncDisposable
{
    public static NoopPaymentLock Instance { get; } = new();

    private NoopPaymentLock() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
