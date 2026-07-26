using Marten;
using Npgsql;
using TicketFlow.Contracts.Messaging.Workflows;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Checkout;
using TicketFlow.Ordering.Application.Workflows;

namespace TicketFlow.Ordering.Api.Services;

public sealed class OrderDispatchOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 50;
}

/// <summary>
/// Drains the <see cref="OrderDispatch"/> outbox to the broker. This is the only thing that
/// publishes <c>OrderSubmitted</c>; the checkout request itself never touches RabbitMQ, so a
/// broker outage can no longer strand an order that is already durable.
/// </summary>
/// <remarks>
/// Publish first, then mark dispatched. A crash in that window republishes on the next pass, so
/// delivery is at-least-once — the honest guarantee, since marking first would instead lose the
/// message entirely, and no ordering of two systems makes it exactly-once. The saga tolerates the
/// repeat: <c>OrderSubmitted</c> correlates by <c>OrderId</c> and only starts an instance
/// <c>Initially</c>, so a duplicate finds a saga already past that state. Commit 8's per-order
/// inbox is what turns that from "tolerated" into "asserted".
/// </remarks>
public sealed class OrderDispatchRelay(
    IServiceScopeFactory scopeFactory,
    OrderDispatchOptions options,
    TimeProvider timeProvider,
    ILogger<OrderDispatchRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[ordering] outbox relay pass failed.");
            }

            try
            {
                await Task.Delay(options.Interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<int> RelayOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<ICheckoutOperationStore>();
        var publisher = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var pending = await operations.GetPendingDispatchesAsync(options.BatchSize, cancellationToken);
        if (pending.Count == 0)
            return 0;

        var dispatched = 0;
        foreach (var record in pending)
        {
            // Non-blocking: if another replica already owns this order, leave it alone rather than
            // queue up behind it and publish the same message twice in quick succession.
            if (!await TryClaimAsync(session, record.Id, cancellationToken))
                continue;

            try
            {
                await publisher.PublishAsync(record.Payload, cancellationToken);
                await WakeCancellationCampaignsAsync(
                    session,
                    publisher,
                    record.Payload.EventId,
                    cancellationToken);
                record.MarkDispatched(timeProvider.GetUtcNow());
                session.Store(record);
                await session.SaveChangesAsync(cancellationToken);
                CheckoutOperationMetrics.RecordDispatched();
                dispatched++;
            }
            catch (Exception exception)
            {
                CheckoutOperationMetrics.RecordDispatchFailure();
                logger.LogWarning(
                    exception,
                    "[ordering] could not relay OrderSubmitted for order {OrderId}; it stays pending.",
                    record.Id);

                record.RecordFailure(exception.Message);
                session.Store(record);
                await session.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                await ReleaseAsync(session, record.Id, cancellationToken);
            }
        }

        return dispatched;
    }

    private async Task WakeCancellationCampaignsAsync(
        IDocumentSession session,
        IIntegrationEventPublisher publisher,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        // A campaign stops scheduling retries after a quiet completion. The durable checkout
        // relay is the one component guaranteed to observe a dispatch committed later, so it also
        // wakes every campaign for that event. Duplicate wakes are harmless and intentionally
        // preferable to leaving a paid order outside cancellation forever.
        var campaigns = await session.Query<EventCancellationCampaign>()
            .Where(campaign => campaign.EventId == eventId)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var campaign in campaigns)
        {
            await publisher.PublishAsync(
                new RetryEventCancellation(campaign.Id, campaign.EventId, now),
                cancellationToken);
        }
    }

    private static async Task<bool> TryClaimAsync(
        IDocumentSession session,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_try_advisory_lock(hashtextextended(@key, 0))");
        command.Parameters.AddWithValue("key", $"order-dispatch:{orderId:N}");
        return await ExecuteScalarAsync(session, command, cancellationToken) is true;
    }

    private static async Task ReleaseAsync(
        IDocumentSession session,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_unlock(hashtextextended(@key, 0))");
        command.Parameters.AddWithValue("key", $"order-dispatch:{orderId:N}");
        await ExecuteScalarAsync(session, command, cancellationToken);
    }

    private static async Task<object?> ExecuteScalarAsync(
        IDocumentSession session,
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)session.Connection!;
        command.Connection = connection;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
