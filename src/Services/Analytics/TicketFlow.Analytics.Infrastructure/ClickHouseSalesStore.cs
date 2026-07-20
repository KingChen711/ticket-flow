using System.Data.Common;
using ClickHouse.Client.ADO;
using TicketFlow.Analytics.Application.Abstractions;
using TicketFlow.Analytics.Application.Sales;
using TicketFlow.Contracts.Messaging.Analytics;

namespace TicketFlow.Analytics.Infrastructure;

public sealed class ClickHouseSalesStore(ClickHouseConnection connection) : ISalesStore
{
    public async Task RecordSaleAsync(SaleRecorded sale, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sales (order_id, event_id, seat_count, amount, currency, price_snapshot_version, completed_at)
            VALUES ({orderId:UUID}, {eventId:UUID}, {seatCount:UInt32}, {amount:Decimal(18,2)}, {currency:FixedString(3)}, {priceSnapshotVersion:UInt64}, {completedAt:DateTime64(3)})
            """;
        AddParam(command, "orderId", sale.OrderId);
        AddParam(command, "eventId", sale.EventId);
        AddParam(command, "seatCount", sale.SeatCount);
        AddParam(command, "amount", sale.Amount);
        AddParam(command, "currency", sale.Currency.ToUpperInvariant());
        AddParam(command, "priceSnapshotVersion", sale.PriceSnapshotVersion);
        AddParam(command, "completedAt", sale.CompletedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// FROM sales FINAL — confirmed via a real probe that ReplacingMergeTree does NOT dedup at
    /// insert time, only on background merge; FINAL forces query-time dedup so a redelivered
    /// SaleRecorded for the same OrderId never inflates revenue, without waiting for/forcing a
    /// merge.
    /// </summary>
    public async Task<RevenueResult> GetRevenueAsync(RevenueQuery query, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();

        var conditions = new List<string>();
        if (query.EventId.HasValue)
        {
            conditions.Add("event_id = {eventId:UUID}");
            AddParam(command, "eventId", query.EventId.Value);
        }
        if (query.From.HasValue)
        {
            conditions.Add("completed_at >= {from:DateTime64(3)}");
            AddParam(command, "from", query.From.Value.UtcDateTime);
        }
        if (query.To.HasValue)
        {
            conditions.Add("completed_at <= {to:DateTime64(3)}");
            AddParam(command, "to", query.To.Value.UtcDateTime);
        }
        if (!string.IsNullOrWhiteSpace(query.Currency))
        {
            conditions.Add("currency = {currency:FixedString(3)}");
            AddParam(command, "currency", query.Currency.ToUpperInvariant());
        }

        var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        command.CommandText = $"""
            SELECT event_id, currency, sum(amount) AS revenue, sum(seat_count) AS seats
            FROM sales FINAL
            {whereClause}
            GROUP BY event_id, currency
            ORDER BY revenue DESC
            """;

        var byEvent = new List<EventRevenue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            byEvent.Add(new EventRevenue(
                reader.GetGuid(0),
                reader.GetString(1).TrimEnd('\0', ' '),
                reader.GetDecimal(2),
                // sum() over a UInt32 column widens to UInt64 in ClickHouse, not Int64 — the
                // driver's GetInt64 does a strict cast and throws InvalidCastException on the
                // real UInt64 value (found via real verification, not something the earlier ADO.NET
                // probe happened to exercise). Convert.ToInt64 handles the widen safely.
                Convert.ToInt64(reader.GetValue(3))));
        }

        return new RevenueResult(
            byEvent.Select(e => e.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? byEvent.Sum(e => e.Revenue)
                : null,
            byEvent.Sum(e => e.SeatCount),
            byEvent,
            byEvent.GroupBy(e => e.Currency, StringComparer.OrdinalIgnoreCase)
                .Select(group => new CurrencyRevenue(group.Key, group.Sum(e => e.Revenue), group.Sum(e => e.SeatCount)))
                .ToList());
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
