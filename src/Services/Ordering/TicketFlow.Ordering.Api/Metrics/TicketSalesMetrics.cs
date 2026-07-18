using System.Diagnostics.Metrics;

namespace TicketFlow.Ordering.Api.Metrics;

/// <summary>
/// Phase 10 Commit 3 business metric — "vé bán/phút" (docs/phase-10-plan.md mục 3). Lives in
/// Ordering, not Analytics: Ordering is the actual owner of an order's lifecycle and the point
/// where a sale becomes irreversible, whereas ClickHouse/Analytics is already a separate read
/// model for revenue reporting — this counter is an operational signal (Grafana `rate()`), not a
/// second copy of the same business-intelligence data.
///
/// Registered as a singleton (not scoped) because `Counter&lt;T&gt;.Add` is thread-safe by design
/// (same reasoning any Meter/Counter in .NET's Diagnostics.Metrics API follows) and there's
/// exactly one Meter for the whole process lifetime — matches how `Meter`/`ActivitySource`
/// instances are conventionally held everywhere else in .NET, not re-created per request.
/// </summary>
public sealed class TicketSalesMetrics : IDisposable
{
    public const string MeterName = "TicketFlow.Ordering";

    private readonly Meter _meter;
    private readonly Counter<long> _ticketsSold;

    public TicketSalesMetrics()
    {
        _meter = new Meter(MeterName);
        _ticketsSold = _meter.CreateCounter<long>(
            "ticketflow.tickets.sold",
            unit: "{ticket}",
            description: "Number of seats sold across completed orders.");
    }

    /// <summary>Called once per <c>OrderCompleted</c> — see <see cref="Consumers.OrderCompletedConsumer"/>,
    /// the point where a saga has driven an order all the way through (seats confirmed → payment
    /// processed → tickets issued), i.e. the seats are irreversibly sold, not just held.</summary>
    public void RecordTicketsSold(int count) => _ticketsSold.Add(count);

    public void Dispose() => _meter.Dispose();
}
