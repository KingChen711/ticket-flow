using Marten;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Infrastructure;

/// <summary>
/// AggregateStreamAsync does "live aggregation": fetch every event in the stream, then fold
/// them by reflecting over Order's Apply overloads — no separate projection registration
/// needed for this. That's a different, explicit thing Commit 5 sets up for the MyOrders read
/// model (a persisted document, kept fast for queries).
/// </summary>
public sealed class MartenOrderStore(IDocumentSession session) : IOrderStore
{
    public Task<Order?> LoadAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        session.Events.AggregateStreamAsync<Order>(orderId, token: cancellationToken);

    public void StartStream(Guid orderId, object initialEvent) =>
        session.Events.StartStream<Order>(orderId, initialEvent);

    public void Append(Guid orderId, object @event) =>
        session.Events.Append(orderId, @event);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        session.SaveChangesAsync(cancellationToken);
}
