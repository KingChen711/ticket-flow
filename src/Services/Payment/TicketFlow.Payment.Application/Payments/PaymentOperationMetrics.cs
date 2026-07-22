using System.Diagnostics.Metrics;

namespace TicketFlow.Payment.Application.Payments;

/// <summary>
/// An <c>Unknown</c> charge is money that may already have left the customer's account while the
/// order sits unfinished — it must be visible as a number, not only as a log line. Static like
/// <c>ContractVersionMetrics</c>; the meter is registered for scraping in Program.cs.
/// </summary>
public static class PaymentOperationMetrics
{
    public const string MeterName = "TicketFlow.Payment";

    private static readonly Meter Meter = new(MeterName, "12.6.0");

    private static readonly Counter<long> Charges =
        Meter.CreateCounter<long>(
            "ticketflow.payment.charge_total",
            unit: "{charge}",
            description: "Charge operations by outcome (authorized/declined/unknown/replayed/claim_lost).");

    private static readonly Counter<long> Refunds =
        Meter.CreateCounter<long>(
            "ticketflow.payment.refund_total",
            unit: "{refund}",
            description: "Refund operations by outcome.");

    private static readonly Counter<long> Reconciliations =
        Meter.CreateCounter<long>(
            "ticketflow.payment.reconcile_total",
            unit: "{operation}",
            description: "Reconciliation passes over ambiguous outcomes, by result.");

    private static readonly Counter<long> ManualReview =
        Meter.CreateCounter<long>(
            "ticketflow.payment.manual_review_total",
            unit: "{operation}",
            description: "Ambiguous operations that exceeded the reconciliation deadline and need a human.");

    public static void RecordCharge(string outcome) =>
        Charges.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordRefund(string outcome) =>
        Refunds.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordReconcile(string kind, string result) =>
        Reconciliations.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("result", result));

    public static void RecordManualReview(string kind) =>
        ManualReview.Add(1, new KeyValuePair<string, object?>("kind", kind));
}
