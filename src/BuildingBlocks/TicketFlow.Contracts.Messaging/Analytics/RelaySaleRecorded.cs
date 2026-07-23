namespace TicketFlow.Contracts.Messaging.Analytics;

/// <summary>
/// Internal hand-off from the purchase saga to Ordering's own Kafka relay (Commit 9).
/// </summary>
/// <remarks>
/// The saga used to call the Kafka producer inline while completing an order, which put an
/// analytics stream on the critical path of a paid, ticketed purchase: a Kafka outage failed the
/// completion activity and the order never reached Confirmed. Publishing this over RabbitMQ
/// instead — through the saga's in-memory outbox, so it is only sent once the saga state has
/// committed — moves the analytics write behind its own retry and DLQ, where an outage costs a
/// delayed metric rather than a stuck order.
/// </remarks>
public sealed record RelaySaleRecorded(
    Guid OrderId,
    Guid EventId,
    int SeatCount,
    decimal Amount,
    DateTimeOffset CompletedAt,
    string Currency,
    long PriceSnapshotVersion);
