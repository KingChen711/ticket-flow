using System.Diagnostics.Metrics;

namespace TicketFlow.Ordering.Application.Checkout;

/// <summary>
/// A replayed checkout and a relayed-late order both look like silence from the outside: the
/// customer gets their order either way. These counters are the only way to notice that clients
/// are retrying, that requests are racing, or that the outbox is falling behind.
/// </summary>
public static class CheckoutOperationMetrics
{
    public const string MeterName = "TicketFlow.Ordering.Checkout";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Accepted =
        Meter.CreateCounter<long>("ticketflow.checkout.accepted", "operations", "Checkouts that created a new order stream.");

    private static readonly Counter<long> Replayed =
        Meter.CreateCounter<long>("ticketflow.checkout.replayed", "operations", "Retries answered from the durable operation record.");

    private static readonly Counter<long> Conflicts =
        Meter.CreateCounter<long>("ticketflow.checkout.conflicts", "operations", "Idempotency keys reused for a different request.");

    private static readonly Counter<long> Races =
        Meter.CreateCounter<long>("ticketflow.checkout.races", "operations", "Concurrent checkouts resolved by the unique constraint.");

    private static readonly Counter<long> Dispatched =
        Meter.CreateCounter<long>("ticketflow.checkout.dispatched", "messages", "OrderSubmitted messages relayed from the outbox.");

    private static readonly Counter<long> DispatchFailures =
        Meter.CreateCounter<long>("ticketflow.checkout.dispatch_failures", "messages", "Outbox relay attempts that could not reach the broker.");

    public static void RecordAccepted() => Accepted.Add(1);

    public static void RecordReplayed() => Replayed.Add(1);

    public static void RecordConflict() => Conflicts.Add(1);

    public static void RecordRace() => Races.Add(1);

    public static void RecordDispatched() => Dispatched.Add(1);

    public static void RecordDispatchFailure() => DispatchFailures.Add(1);
}
