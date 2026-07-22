using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// Mirrors Marten's IDocumentSession event API shape (stage writes, commit via
/// SaveChangesAsync) — same buffer-then-commit pattern as IUnitOfWork elsewhere in this repo,
/// just for an event stream instead of an EF change tracker. Application depends on this port,
/// not on Marten directly; Infrastructure (Commit 2) provides the Marten-backed implementation.
/// </summary>
public interface IOrderStore
{
    /// <summary>Folds the full event history for orderId — null if no stream exists.</summary>
    Task<Order?> LoadAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Stages a brand-new stream's first event. Not persisted until SaveChangesAsync.</summary>
    void StartStream(Guid orderId, object initialEvent);

    /// <summary>Stages an event onto an existing stream. Not persisted until SaveChangesAsync.</summary>
    void Append(Guid orderId, object @event);

    /// <summary>
    /// Opens the stream for a read-decide-append cycle checked against the version it was read at
    /// (Commit 8). A plain <see cref="Append"/> is a blind write: two consumers that both folded
    /// version N both append at N+1, and the order ends up with two terminal events. Returns null
    /// when the stream does not exist.
    /// </summary>
    Task<IOrderStreamWriter?> FetchForWritingAsync(Guid orderId, CancellationToken cancellationToken = default)
        => Task.FromResult<IOrderStreamWriter?>(null);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the commit lost a version race. The caller reloads and re-decides rather than
    /// retrying the same append, because the winning writer may have already made this decision
    /// obsolete.
    /// </summary>
    bool IsConcurrencyConflict(Exception exception) => false;
}

/// <summary>An open stream, plus the version it was read at.</summary>
public interface IOrderStreamWriter
{
    Order Order { get; }

    long Version { get; }

    void Append(object @event);
}
