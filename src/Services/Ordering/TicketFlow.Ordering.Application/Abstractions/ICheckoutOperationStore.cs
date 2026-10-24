using TicketFlow.Ordering.Application.Checkout;

namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// Durable checkout bookkeeping. Deliberately shares <see cref="IOrderStore"/>'s
/// stage-then-commit shape and, in the Marten implementation, the very same
/// <c>IDocumentSession</c> — the operation record, the outbox row and the order stream must land
/// in one transaction or the guarantee is worthless.
/// </summary>
public interface ICheckoutOperationStore
{
    Task<CheckoutOperation?> FindOperationAsync(string key, CancellationToken cancellationToken = default);

    Task<CheckoutOperation?> FindOperationByHoldAsync(Guid holdId, CancellationToken cancellationToken = default);

    void StoreOperation(CheckoutOperation operation);

    void StoreDispatch(OrderDispatch dispatch);

    Task<IReadOnlyList<OrderDispatch>> GetPendingDispatchesAsync(
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>True when Marten/Postgres is refusing a duplicate key, whatever wrapper exposed it.</summary>
    bool IsUniqueViolation(Exception exception);

    /// <summary>
    /// Drops writes that were staged but not committed. A failed commit leaves them queued in the
    /// session, so without this the next <c>SaveChangesAsync</c> anywhere in the same scope would
    /// replay the insert that just lost its race.
    /// </summary>
    void DiscardPendingChanges();
}
