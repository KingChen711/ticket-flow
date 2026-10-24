using System.Text.Json;
using Marten;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TicketFlow.Ordering.Infrastructure.Messaging;

/// <summary>
/// Publishes committed <see cref="OutboxMessage"/>s in creation order and marks each one sent
/// right after its publish. Delivery is at-least-once: a crash between the publish and the mark
/// sends that message again on restart, so consumers of outbox messages must tolerate duplicates.
/// Assumes a single Ordering instance; several instances could publish the same pending message.
/// </summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopeFactory, IBus bus, TimeProvider timeProvider, ILogger<OutboxDispatcher> logger)
    : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                sent = await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Broker or database unavailable: keep the messages and try again on the next tick.
                logger.LogWarning(ex, "Outbox dispatch failed; pending messages will be retried.");
            }

            if (sent < BatchSize)
                await Task.Delay(IdleDelay, timeProvider, stoppingToken);
        }
    }

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var pending = await session.Query<OutboxMessage>()
            .Where(message => message.SentAt == null)
            .OrderBy(message => message.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            var type = Type.GetType(message.MessageType, throwOnError: true)!;
            var payload = JsonSerializer.Deserialize(message.Payload, type)!;
            await bus.Publish(payload, type, cancellationToken);

            message.SentAt = timeProvider.GetUtcNow();
            session.Store(message);
            await session.SaveChangesAsync(cancellationToken);
        }

        return pending.Count;
    }
}
