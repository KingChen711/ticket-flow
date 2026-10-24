using System.Diagnostics.Metrics;

namespace TicketFlow.Ticketing.Application.Abstractions;

/// <summary>
/// A transient issuance fault is now silent by design — it throws and is retried, publishing
/// nothing. That is the right behaviour and exactly why it needs a counter: without one, a renderer
/// failing every single time looks identical to a renderer that is working.
/// </summary>
public static class TicketIssuanceMetrics
{
    public const string MeterName = "TicketFlow.Ticketing";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Issued =
        Meter.CreateCounter<long>("ticketflow.tickets.issued", "{ticket}", "Tickets rendered and persisted.");

    private static readonly Counter<long> TransientFailures =
        Meter.CreateCounter<long>("ticketflow.tickets.transient_failures", "orders", "Issuance attempts left for redelivery.");

    private static readonly Counter<long> PermanentFailures =
        Meter.CreateCounter<long>("ticketflow.tickets.permanent_failures", "orders", "Orders whose issuance was refused for good.");

    private static readonly Counter<long> Races =
        Meter.CreateCounter<long>("ticketflow.tickets.issuance_races", "orders", "Concurrent issuance attempts stopped by the claim.");

    private static readonly Counter<long> RefusedScans =
        Meter.CreateCounter<long>("ticketflow.tickets.refused_scans", "{scan}", "Gate scans refused, by reason.");

    public static void RecordIssued(int count) => Issued.Add(count);

    public static void RecordTransientFailure() => TransientFailures.Add(1);

    public static void RecordFailed() => PermanentFailures.Add(1);

    public static void RecordRace() => Races.Add(1);

    public static void RecordRefusedScan(string reason) =>
        RefusedScans.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
