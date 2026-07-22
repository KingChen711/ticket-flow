using System.Diagnostics.Metrics;

namespace TicketFlow.Ordering.Application.Orders;

/// <summary>
/// An ignored milestone is the writer working correctly, and a version conflict is the guard
/// firing — both are silent by design, so both need a number. A conflict rate that climbs is the
/// early warning that the single-writer funnel is being bypassed again.
/// </summary>
public static class OrderMilestoneMetrics
{
    public const string MeterName = "TicketFlow.Ordering.Milestones";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Appended =
        Meter.CreateCounter<long>("ticketflow.order.milestone_appended", "events", "Milestones appended to an order stream.");

    private static readonly Counter<long> Conflicts =
        Meter.CreateCounter<long>("ticketflow.order.milestone_conflicts", "events", "Appends that lost a stream version race and were re-decided.");

    public static void RecordAppended(string milestone) =>
        Appended.Add(1, new KeyValuePair<string, object?>("milestone", milestone));

    public static void RecordConflict(string milestone) =>
        Conflicts.Add(1, new KeyValuePair<string, object?>("milestone", milestone));
}
