using TicketFlow.Analytics.Application.Sales;
using TicketFlow.Contracts.Messaging.Analytics;

namespace TicketFlow.Analytics.Application.Abstractions;

/// <summary>
/// Application knows it needs "record this sale fact" and "aggregate revenue", not HOW
/// (ClickHouse in Infrastructure). Idempotency is a storage-engine property here (ReplacingMergeTree
/// keyed on OrderId), not a middleware concern — contrast with Phase 5's EF Inbox for Postgres
/// (Phase 6 mục 4). RecordSaleAsync is expected to be called for the same OrderId more than once
/// (at-least-once Kafka delivery) without inflating revenue.
/// </summary>
public interface ISalesStore
{
    Task RecordSaleAsync(SaleRecorded sale, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records which organizer owns an event (Phase 12 Commit 14), so revenue can be scoped to the
    /// caller's own events. Same idempotency story as <see cref="RecordSaleAsync"/> — the storage
    /// engine collapses repeats, the caller does not need to check first.
    /// </summary>
    Task RecordEventOwnerAsync(
        Guid eventId,
        Guid organizerId,
        long revision,
        CancellationToken cancellationToken = default);

    Task<RevenueResult> GetRevenueAsync(RevenueQuery query, CancellationToken cancellationToken = default);
}
