namespace TicketFlow.Contracts.Messaging.Analytics;

/// <summary>
/// Kafka topic message, not a RabbitMQ integration event — published by Ordering's PurchaseSaga
/// at the same business moment as OrderCompleted (RabbitMQ, for notification/read-model
/// consumers), but over a different transport for a different purpose: an analytics stream
/// consumers can replay, not a one-shot command/notification. Denormalized fact record (no
/// lookups needed downstream) — Analytics (Commit 7) inserts this shape directly into ClickHouse.
/// </summary>
public sealed record SaleRecorded(Guid OrderId, Guid EventId, int SeatCount, decimal Amount, DateTimeOffset CompletedAt);
