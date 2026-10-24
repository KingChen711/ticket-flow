using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Search.Application.Abstractions;

namespace TicketFlow.Search.Api.Consumers;

/// <summary>
/// Removes the event from search RESULTS entirely (phase-6-plan.md architecture decision 2 —
/// "nghiêng về xoá khỏi kết quả tìm"): a cancelled event isn't purchasable, so it has no reason to
/// still show up in a customer search. Catalog itself remains the durable record.
/// </summary>
/// <remarks>
/// Commit 11 changed HOW that removal is done: a literal delete forgets the event ever existed,
/// so an out-of-order Published arriving afterwards would recreate the document and resurrect a
/// cancelled event in search results. ApplyCancelledAsync instead tombstones the document — kept
/// internally, but treated as absent by every reader (GetByIdAsync/SearchAsync both exclude it) —
/// so the "removed from results" behavior is unchanged while the fact that it was cancelled is
/// remembered forever and can never be overwritten. Idempotent: cancelling an already-tombstoned
/// or never-indexed id is a safe no-op, so redelivery needs no guard here.
/// </remarks>
public sealed class CatalogEventCancelledConsumer(IEventSearchIndex searchIndex) : IConsumer<CatalogEventCancelled>
{
    public Task Consume(ConsumeContext<CatalogEventCancelled> context)
    {
        ContractVersionMetrics.RecordInbound(nameof(CatalogEventCancelled), context.Message.AggregateVersion);
        return searchIndex.ApplyCancelledAsync(
            context.Message.CatalogEventId, context.Message.AggregateVersion, context.CancellationToken);
    }
}
