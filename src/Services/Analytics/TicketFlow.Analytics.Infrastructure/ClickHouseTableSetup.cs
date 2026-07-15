using ClickHouse.Client.ADO;

namespace TicketFlow.Analytics.Infrastructure;

/// <summary>
/// Dev-only setup-on-startup, same spirit as ElasticsearchIndexSetup/EF migrate-on-startup
/// elsewhere in this repo. ReplacingMergeTree(completed_at) keyed by ORDER BY order_id — a
/// redelivered SaleRecorded for the same OrderId collapses on background merge (Phase 6 mục 4:
/// idempotency as a storage-engine property, not middleware). Confirmed via a real probe that
/// dedup does NOT happen at insert time — only on merge or when a query reads with FINAL, which
/// is why ClickHouseSalesStore's revenue query always reads FROM sales FINAL.
/// </summary>
public static class ClickHouseTableSetup
{
    public static async Task EnsureCreatedAsync(ClickHouseConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sales (
                order_id UUID,
                event_id UUID,
                seat_count UInt32,
                amount Decimal(18,2),
                completed_at DateTime64(3)
            ) ENGINE = ReplacingMergeTree(completed_at)
            ORDER BY order_id
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
