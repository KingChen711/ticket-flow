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
            .Where(message => message.SentAt == null && message.FailedAt == null)
            .OrderBy(message => message.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            if (!TryRead(message, out var payload, out var type, out var error))
            {
                // Retrying cannot fix a row like this, and it must not hold back the ones behind it.
                logger.LogError("Outbox message {MessageId} ({MessageType}) cannot be sent and was set aside: {Error}",
                    message.Id, message.MessageType, error);
                message.FailedAt = timeProvider.GetUtcNow();
                message.Error = error;
            }
            else
            {
                // A publish failure (broker down) propagates and the batch is retried on the next tick.
                await bus.Publish(payload, type, cancellationToken);
                message.SentAt = timeProvider.GetUtcNow();
            }

            session.Store(message);
            await session.SaveChangesAsync(cancellationToken);
        }

        return pending.Count;
    }

    private static bool TryRead(OutboxMessage message, out object payload, out Type type, out string? error)
    {
        payload = null!;
        type = null!;
        try
        {
            var resolved = Type.GetType(message.MessageType, throwOnError: false);
            if (resolved is null)
            {
                error = "Message type could not be resolved.";
                return false;
            }

            var read = JsonSerializer.Deserialize(message.Payload, resolved);
            if (read is null)
            {
                error = "Payload deserialized to null.";
                return false;
            }

            (payload, type, error) = (read, resolved, null);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Payload is not valid JSON for the type: {ex.Message}";
            return false;
        }
    }
}
