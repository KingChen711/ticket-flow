using Marten;
using MassTransit;
using Npgsql;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Sagas;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>
/// Durable marker for one order's analytics relay. Keyed by OrderId, so two concurrent deliveries
/// cannot both reach Kafka.
/// </summary>
public sealed class SaleRelayRecord
{
    public Guid Id { get; set; }

    public DateTimeOffset ClaimedAt { get; set; }

    /// <summary>Null until Kafka has accepted the produce. Only a set value means "done".</summary>
    public DateTimeOffset? RelayedAt { get; set; }
}

/// <summary>
/// Relays a completed sale to the Kafka analytics stream, off the purchase saga's critical path
/// (Commit 9).
/// </summary>
/// <remarks>
/// Claim before produce, the same shape as Payment's charge (Commit 6): the marker row is
/// committed first, so two concurrent deliveries cannot both reach Kafka. The claim is then
/// completed only after Kafka accepts, because a claim alone must not be mistaken for a finished
/// relay — that would drop the sale whenever a produce failed.
///
/// The honest guarantee is therefore: never two sales from concurrent delivery or from a failed
/// produce, but a crash in the narrow window between a successful produce and its completion mark
/// will relay that one sale twice. Closing that window needs an idempotent producer key, which the
/// analytics topic does not have; recording it here is better than implying exactly-once.
/// </remarks>
public sealed class SaleRelayConsumer(
    IDocumentSession session,
    ISaleRecordedPublisher publisher,
    ILogger<SaleRelayConsumer> logger) : IConsumer<RelaySaleRecorded>
{
    public async Task Consume(ConsumeContext<RelaySaleRecorded> context)
    {
        var message = context.Message;
        var record = await session.LoadAsync<SaleRelayRecord>(message.OrderId, context.CancellationToken);

        // Only a completed relay ends the work. A claim on its own means a previous attempt got as
        // far as reserving the sale and no further — returning here would silently lose it.
        if (record?.RelayedAt is not null)
            return;

        if (record is null)
        {
            session.Insert(new SaleRelayRecord { Id = message.OrderId, ClaimedAt = DateTimeOffset.UtcNow });
            try
            {
                await session.SaveChangesAsync(context.CancellationToken);
            }
            catch (Exception exception) when (IsUniqueViolation(exception))
            {
                // A concurrent delivery claimed it first; that one owns the produce.
                logger.LogInformation("[ordering] sale for order {OrderId} is already claimed.", message.OrderId);
                return;
            }
        }

        // A produce failure throws, so the message is retried and eventually lands in the DLQ for an
        // operator to redrive. The order itself is already Confirmed and entirely unaffected — which
        // is the whole point of this consumer existing.
        await publisher.PublishAsync(
            new SaleRecorded(
                message.OrderId,
                message.EventId,
                message.SeatCount,
                message.Amount,
                message.CompletedAt,
                message.Currency,
                message.PriceSnapshotVersion),
            context.CancellationToken);

        record ??= await session.LoadAsync<SaleRelayRecord>(message.OrderId, context.CancellationToken);
        record!.RelayedAt = DateTimeOffset.UtcNow;
        session.Store(record);
        await session.SaveChangesAsync(context.CancellationToken);

        PurchaseSagaMetrics.RecordSaleRelayed();
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        }

        return false;
    }
}
