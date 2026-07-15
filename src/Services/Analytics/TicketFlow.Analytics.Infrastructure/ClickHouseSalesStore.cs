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
            INSERT INTO sales (order_id, event_id, seat_count, amount, completed_at)
            VALUES ({orderId:UUID}, {eventId:UUID}, {seatCount:UInt32}, {amount:Decimal(18,2)}, {completedAt:DateTime64(3)})
            """;
        AddParam(command, "orderId", sale.OrderId);
        AddParam(command, "eventId", sale.EventId);
        AddParam(command, "seatCount", sale.SeatCount);
        AddParam(command, "amount", sale.Amount);
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

        var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        command.CommandText = $"""
            SELECT event_id, sum(amount) AS revenue, sum(seat_count) AS seats
            FROM sales FINAL
            {whereClause}
            GROUP BY event_id
            ORDER BY revenue DESC
            """;

        var byEvent = new List<EventRevenue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            byEvent.Add(new EventRevenue(
                reader.GetGuid(0),
                reader.GetDecimal(1),
                // sum() over a UInt32 column widens to UInt64 in ClickHouse, not Int64 — the
                // driver's GetInt64 does a strict cast and throws InvalidCastException on the
                // real UInt64 value (found via real verification, not something the earlier ADO.NET
                // probe happened to exercise). Convert.ToInt64 handles the widen safely.
                Convert.ToInt64(reader.GetValue(2))));
        }

        return new RevenueResult(
            byEvent.Sum(e => e.Revenue),
            byEvent.Sum(e => e.SeatCount),
            byEvent);
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
