using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Tests;

/// <summary>
/// In-memory stand-in for MartenOrderStore — lets handler tests verify orchestration (does the
/// handler call StartStream vs Append correctly, does it throw when a stream doesn't exist)
/// without touching Postgres. Marten's own event-store behaviour is already verified for real
/// in Commit 2; this fake exists purely to isolate handler logic.
/// </summary>
public sealed class FakeOrderStore : IOrderStore
{
    private readonly Dictionary<Guid, List<object>> _streams = [];

    public IReadOnlyList<object> EventsFor(Guid orderId) => _streams[orderId];

    public Task<Order?> LoadAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_streams.TryGetValue(orderId, out var events) ? Order.Load(events) : null);

    public void StartStream(Guid orderId, object initialEvent) => _streams[orderId] = [initialEvent];

    public void Append(Guid orderId, object @event) => _streams[orderId].Add(@event);

    /// <summary>
    /// Models the version-checked write path the admin handlers use (Commit 9 audit). The version
    /// itself is not contended in these tests — what matters is that the handlers go through this
    /// path at all, rather than loading and appending blindly.
    /// </summary>
    public Task<IOrderStreamWriter?> FetchForWritingAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IOrderStreamWriter?>(
            _streams.TryGetValue(orderId, out var events)
                ? new Writer(Order.Load(events), events.Count, events)
                : null);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed class Writer(Order order, long version, List<object> events) : IOrderStreamWriter
    {
        public Order Order { get; } = order;
        public long Version { get; } = version;
        public void Append(object @event) => events.Add(@event);
    }
}
