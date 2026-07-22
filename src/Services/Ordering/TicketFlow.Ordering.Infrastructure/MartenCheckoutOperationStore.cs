using Marten;
using Npgsql;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Checkout;

namespace TicketFlow.Ordering.Infrastructure;

/// <summary>
/// Shares the scoped <c>IDocumentSession</c> with <see cref="MartenOrderStore"/> on purpose: the
/// order stream, the checkout receipt and the outbox row are staged into the same session and
/// committed by a single <c>SaveChangesAsync</c>. Injecting a second session here would quietly
/// reintroduce the dual write this commit exists to remove.
/// </summary>
public sealed class MartenCheckoutOperationStore(IDocumentSession session) : ICheckoutOperationStore
{
    public Task<CheckoutOperation?> FindOperationAsync(string key, CancellationToken cancellationToken = default) =>
        session.LoadAsync<CheckoutOperation>(key, cancellationToken);

    public async Task<CheckoutOperation?> FindOperationByHoldAsync(
        Guid holdId,
        CancellationToken cancellationToken = default) =>
        await session.Query<CheckoutOperation>()
            .SingleOrDefaultAsync(operation => operation.HoldId == holdId, cancellationToken);

    /// <summary>
    /// <c>Insert</c>, not <c>Store</c>. Marten's <c>Store</c> is an upsert, so a racing second
    /// checkout would quietly overwrite the winner's receipt and point the key at a second order —
    /// the exact duplicate this record exists to prevent. <c>Insert</c> lets Postgres refuse it.
    /// </summary>
    public void StoreOperation(CheckoutOperation operation) => session.Insert(operation);

    public void StoreDispatch(OrderDispatch dispatch) => session.Insert(dispatch);

    public async Task<IReadOnlyList<OrderDispatch>> GetPendingDispatchesAsync(
        int maxCount,
        CancellationToken cancellationToken = default) =>
        await session.Query<OrderDispatch>()
            .Where(dispatch => dispatch.State == OrderDispatchState.Pending)
            .OrderBy(dispatch => dispatch.CreatedAt)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

    public void DiscardPendingChanges() => session.EjectAllPendingChanges();

    /// <summary>
    /// Marten wraps the driver error, so the Postgres SQLSTATE has to be dug out of the inner
    /// exception chain rather than matched on Marten's own exception type.
    /// </summary>
    public bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        }

        return false;
    }
}
