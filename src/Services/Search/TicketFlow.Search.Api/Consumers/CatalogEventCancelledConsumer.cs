using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Search.Application.Abstractions;

namespace TicketFlow.Search.Api.Consumers;

/// <summary>
/// Removes the event from search results entirely, rather than marking it Cancelled and keeping
/// it indexed (phase-6-plan.md architecture decision 2 — "nghiêng về xoá khỏi kết quả tìm"). A
/// cancelled event isn't purchasable, so it has no reason to still show up in a customer search;
/// catalog itself remains the durable record (search is only ever a rebuildable read model).
/// DeleteAsync on an already-deleted/never-indexed id is a no-op in Elasticsearch (404, not an
/// exception this consumer needs to guard against) — safe to redeliver.
/// </summary>
public sealed class CatalogEventCancelledConsumer(IEventSearchIndex searchIndex) : IConsumer<CatalogEventCancelled>
{
    public Task Consume(ConsumeContext<CatalogEventCancelled> context) =>
        searchIndex.DeleteAsync(context.Message.CatalogEventId, context.CancellationToken);
}
