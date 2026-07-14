using Marten;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Infrastructure;

/// <summary>
/// AggregateStreamAsync does "live aggregation": fetch every event in the stream, then fold
/// them via the compile-time source-generated Apply dispatcher (Commit 2) — no separate
/// projection registration needed for this. That's a different, explicit thing Commit 5 sets up
/// for the MyOrders read model (a persisted document, kept fast for queries).
/// </summary>
public sealed class MartenOrderStore(IDocumentSession session) : IOrderStore
{
    public async Task<Order?> LoadAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        // Real bug caught via HTTP verification (Commit 4): Marten's AggregateStreamAsync does
        // NOT treat Guid.Empty as "stream not found" the way it does a genuinely unknown Guid —
        // it folds events with effectively no stream filter, silently returning state mixed in
        // from OTHER orders' events instead of null (a real cross-aggregate data leak if a
        // client ever sends an empty/uninitialized id). A real order id is always a fresh
        // Guid.NewGuid() from Order.Place, so Guid.Empty can never be legitimate — short-circuit
        // it here, at the boundary where Marten's behavior meets IOrderStore's actual contract
        // ("returns null if the stream doesn't exist"), so every caller is protected.
        if (orderId == Guid.Empty)
            return null;

        return await session.Events.AggregateStreamAsync<Order>(orderId, token: cancellationToken);
    }

    public void StartStream(Guid orderId, object initialEvent) =>
        session.Events.StartStream<Order>(orderId, initialEvent);

    public void Append(Guid orderId, object @event) =>
        session.Events.Append(orderId, @event);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        session.SaveChangesAsync(cancellationToken);
}
