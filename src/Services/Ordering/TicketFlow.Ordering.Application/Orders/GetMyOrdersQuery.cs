using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Orders;

public sealed record GetMyOrdersQuery(Guid CustomerId);

public sealed class GetMyOrdersQueryHandler(IOrderViewStore store) : IQueryHandler<GetMyOrdersQuery, IReadOnlyList<MyOrderView>>
{
    public Task<IReadOnlyList<MyOrderView>> HandleAsync(GetMyOrdersQuery query, CancellationToken cancellationToken = default) =>
        store.GetByCustomerAsync(query.CustomerId, cancellationToken);
}
