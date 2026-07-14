using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Orders;

public sealed record CancelOrderCommand(Guid OrderId, string Reason);

public sealed class CancelOrderCommandHandler(IOrderStore store, TimeProvider timeProvider)
    : ICommandHandler<CancelOrderCommand, OrderResponse>
{
    public async Task<OrderResponse> HandleAsync(CancelOrderCommand command, CancellationToken cancellationToken = default)
    {
        var order = await store.LoadAsync(command.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order '{command.OrderId}' was not found.");

        var cancelled = order.Cancel(command.Reason, timeProvider.GetUtcNow());
        store.Append(command.OrderId, cancelled);
        await store.SaveChangesAsync(cancellationToken);

        order.Apply(cancelled);
        return OrderResponseMapper.Map(order);
    }
}
