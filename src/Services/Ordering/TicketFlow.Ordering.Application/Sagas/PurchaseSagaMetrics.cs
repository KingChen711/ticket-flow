using System.Diagnostics.Metrics;

namespace TicketFlow.Ordering.Application.Sagas;

/// <summary>
/// A deadline firing is the system noticing something no log line would otherwise show: a
/// participant that simply never answered. Manual review is stronger still — it means automation
/// has given up on a real customer's order, which must page someone rather than sit in a table.
/// </summary>
public static class PurchaseSagaMetrics
{
    public const string MeterName = "TicketFlow.Ordering.Saga";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Deadlines =
        Meter.CreateCounter<long>("ticketflow.saga.deadlines", "events", "Deadlines that fired while waiting on a participant.");

    private static readonly Counter<long> ManualReviews =
        Meter.CreateCounter<long>("ticketflow.saga.manual_review", "orders", "Orders handed to an operator after exhausting retries.");

    private static readonly Counter<long> MissingInstanceRedeliveries =
        Meter.CreateCounter<long>("ticketflow.saga.missing_instance_redeliveries", "messages", "Participant answers that arrived before their saga existed.");

    public static void RecordDeadline(string state) =>
        Deadlines.Add(1, new KeyValuePair<string, object?>("state", state));

    public static void RecordManualReview(string state) =>
        ManualReviews.Add(1, new KeyValuePair<string, object?>("state", state));

    private static readonly Counter<long> SalesRelayed =
        Meter.CreateCounter<long>("ticketflow.saga.sales_relayed", "sales", "Completed sales handed to the Kafka analytics stream.");

    public static void RecordSaleRelayed() => SalesRelayed.Add(1);

    private static readonly Counter<long> Resumes =
        Meter.CreateCounter<long>("ticketflow.saga.resumes", "orders", "Orders an operator pushed back into automation.");

    public static void RecordResume(string requestedBy) =>
        Resumes.Add(1, new KeyValuePair<string, object?>("requested_by", requestedBy));

    public static void RecordMissingInstanceRedelivery(string messageType) =>
        MissingInstanceRedeliveries.Add(1, new KeyValuePair<string, object?>("message", messageType));
}
