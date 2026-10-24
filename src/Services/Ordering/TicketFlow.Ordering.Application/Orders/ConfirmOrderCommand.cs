using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Orders;

public sealed record ConfirmOrderCommand(Guid OrderId);

/// <summary>
/// Admin override. Kept on the version-checked write path (Commit 9 audit) — it used to load the
/// stream and then append blindly, which is the exact race Commit 8 removed from the milestone
/// consumers: an operator confirming while the saga was completing the same order could append two
/// terminal events. <c>Order.Confirm</c>'s own guard rejects a non-Pending order, so losing the
/// race now surfaces as that rejection instead of a second transition.
/// </summary>
public sealed class ConfirmOrderCommandHandler(IOrderStore store, TimeProvider timeProvider)
    : ICommandHandler<ConfirmOrderCommand, OrderResponse>
{
    public async Task<OrderResponse> HandleAsync(ConfirmOrderCommand command, CancellationToken cancellationToken = default)
    {
        var writer = await store.FetchForWritingAsync(command.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order '{command.OrderId}' was not found.");

        var confirmed = writer.Order.Confirm(timeProvider.GetUtcNow());
        writer.Append(confirmed);
        await store.SaveChangesAsync(cancellationToken);

        // Apply is the single mutator (Commit 1) — reusing it here reflects the just-appended
        // event into this in-memory instance so the response DTO shows the new status without a
        // round-trip reload from Marten.
        writer.Order.Apply(confirmed);
        return OrderResponseMapper.Map(writer.Order);
    }
}
