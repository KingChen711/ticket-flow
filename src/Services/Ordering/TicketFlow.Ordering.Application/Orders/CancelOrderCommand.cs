using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Orders;

public sealed record CancelOrderCommand(Guid OrderId, string Reason);

/// <summary>
/// Admin override. On the version-checked write path for the same reason as
/// <see cref="ConfirmOrderCommandHandler"/> (Commit 9 audit): an operator cancelling while the saga
/// was confirming the same order could otherwise leave the stream with both outcomes.
/// </summary>
public sealed class CancelOrderCommandHandler(IOrderStore store, TimeProvider timeProvider)
    : ICommandHandler<CancelOrderCommand, OrderResponse>
{
    public async Task<OrderResponse> HandleAsync(CancelOrderCommand command, CancellationToken cancellationToken = default)
    {
        var writer = await store.FetchForWritingAsync(command.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order '{command.OrderId}' was not found.");

        var cancelled = writer.Order.Cancel(command.Reason, timeProvider.GetUtcNow());
        writer.Append(cancelled);
        await store.SaveChangesAsync(cancellationToken);

        writer.Order.Apply(cancelled);
        return OrderResponseMapper.Map(writer.Order);
    }
}
