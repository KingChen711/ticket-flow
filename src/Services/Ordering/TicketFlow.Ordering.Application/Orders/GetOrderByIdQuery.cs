using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Orders;

public sealed record GetOrderByIdQuery(Guid OrderId);

public sealed class GetOrderByIdQueryHandler(IOrderViewStore store) : IQueryHandler<GetOrderByIdQuery, MyOrderView?>
{
    public Task<MyOrderView?> HandleAsync(GetOrderByIdQuery query, CancellationToken cancellationToken = default) =>
        store.GetByIdAsync(query.OrderId, cancellationToken);
}
