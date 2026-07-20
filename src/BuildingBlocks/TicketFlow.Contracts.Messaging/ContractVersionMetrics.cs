using System.Diagnostics.Metrics;

namespace TicketFlow.Contracts.Messaging;

/// <summary>
/// Transitional observability for contract removal.  A consumer records v1 when it receives a
/// payload with no aggregate revision; v2 producers use a revision greater than zero.
/// </summary>
public static class ContractVersionMetrics
{
    public const string MeterName = "TicketFlow.Contracts";

    private static readonly Meter Meter = new(MeterName, "2.0.0");
    private static readonly Counter<long> V1Traffic = Meter.CreateCounter<long>("ticketflow.contract.v1_traffic_total");

    public static void RecordInbound(string contractName, long aggregateVersion)
    {
        if (aggregateVersion == 0)
            V1Traffic.Add(1, new KeyValuePair<string, object?>("contract", contractName));
    }
}
