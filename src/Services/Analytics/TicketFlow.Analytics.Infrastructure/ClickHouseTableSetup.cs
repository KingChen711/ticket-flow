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
                currency FixedString(3) DEFAULT 'VND',
                price_snapshot_version UInt64 DEFAULT 0,
                completed_at DateTime64(3)
            ) ENGINE = ReplacingMergeTree(completed_at)
            ORDER BY order_id
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // CREATE IF NOT EXISTS does not expand a Phase 11 table already on disk.
        command.CommandText = "ALTER TABLE sales ADD COLUMN IF NOT EXISTS currency FixedString(3) DEFAULT 'VND' AFTER amount";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "ALTER TABLE sales ADD COLUMN IF NOT EXISTS price_snapshot_version UInt64 DEFAULT 0 AFTER currency";
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Commit 14: which organizer owns which event, projected from CatalogEventPublished, so
        // /revenue can be scoped without every sale row having to carry an organizer that the
        // order path never knew. ReplacingMergeTree(revision) keyed by event_id for the same
        // reason sales uses one: a redelivered publish collapses onto the same row, and the higher
        // Catalog AggregateVersion is the one that survives a merge.
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS event_owners (
                event_id UUID,
                organizer_id UUID,
                revision UInt64
            ) ENGINE = ReplacingMergeTree(revision)
            ORDER BY event_id
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
