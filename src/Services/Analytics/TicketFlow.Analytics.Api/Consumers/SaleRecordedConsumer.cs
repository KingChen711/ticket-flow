using MassTransit;
using TicketFlow.Analytics.Application.Abstractions;
using TicketFlow.Contracts.Messaging.Analytics;

namespace TicketFlow.Analytics.Api.Consumers;

/// <summary>
/// Idempotent by construction — RecordSaleAsync inserts into ClickHouse's ReplacingMergeTree,
/// which collapses redelivered SaleRecorded for the same OrderId on merge (Phase 6 mục 4).
/// </summary>
public sealed class SaleRecordedConsumer(ISalesStore salesStore) : IConsumer<SaleRecorded>
{
    public Task Consume(ConsumeContext<SaleRecorded> context) =>
        salesStore.RecordSaleAsync(context.Message, context.CancellationToken);
}
