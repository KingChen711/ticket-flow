using Marten;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Orders;

namespace TicketFlow.Ordering.Infrastructure;

/// <summary>
/// IQuerySession, not IDocumentSession — the query side never writes, so it only needs read
/// access to the projected documents MyOrdersProjection (Commit 5) keeps up to date.
/// </summary>
public sealed class MartenOrderViewStore(IQuerySession session) : IOrderViewStore
{
    public Task<MyOrderView?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        session.LoadAsync<MyOrderView>(orderId, cancellationToken);

    public async Task<IReadOnlyList<MyOrderView>> GetByCustomerAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        await session.Query<MyOrderView>()
            .Where(view => view.CustomerId == customerId)
            .ToListAsync(cancellationToken);
}
