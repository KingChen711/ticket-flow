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

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
