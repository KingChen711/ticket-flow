using TicketFlow.Ordering.Application.Orders;

namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// Deliberately separate from IOrderStore (Commit 2) — that port reads by folding the event
/// stream (the write side's concern); this one reads the already-projected MyOrderView document
/// directly. Two ports for two models is the CQRS split made literal in the port layer, not just
/// in which methods get called.
/// </summary>
public interface IOrderViewStore
{
    Task<MyOrderView?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MyOrderView>> GetByCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);
}
