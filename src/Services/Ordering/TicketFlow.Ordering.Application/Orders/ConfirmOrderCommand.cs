using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Orders;

public sealed record ConfirmOrderCommand(Guid OrderId);

public sealed class ConfirmOrderCommandHandler(IOrderStore store, TimeProvider timeProvider)
    : ICommandHandler<ConfirmOrderCommand, OrderResponse>
{
    public async Task<OrderResponse> HandleAsync(ConfirmOrderCommand command, CancellationToken cancellationToken = default)
    {
        var order = await store.LoadAsync(command.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order '{command.OrderId}' was not found.");

        var confirmed = order.Confirm(timeProvider.GetUtcNow());
        store.Append(command.OrderId, confirmed);
        await store.SaveChangesAsync(cancellationToken);

        // Apply is the single mutator (Commit 1) — reusing it here reflects the just-appended
        // event into this in-memory instance so the response DTO shows the new status without a
        // round-trip reload from Marten.
        order.Apply(confirmed);
        return OrderResponseMapper.Map(order);
    }
}
