using System.Text.Json;
using Marten;
using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Infrastructure.Messaging;

/// <summary>
/// Transactional outbox for Ordering. MassTransit.Marten ships a saga repository but no outbox, so
/// publishing straight to RabbitMQ after SaveChangesAsync could lose OrderSubmitted on a crash or a
/// broker outage and leave the order Pending forever. Here PublishAsync only stages the message in
/// the scoped Marten session; it is committed together with the caller's events by
/// SaveChangesAsync and sent later by <see cref="OutboxDispatcher"/>.
/// </summary>
public sealed class MartenOutboxPublisher(IDocumentSession session, TimeProvider timeProvider) : IIntegrationEventPublisher
{
    public Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default) where T : class
    {
        session.Store(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageType = integrationEvent.GetType().AssemblyQualifiedName!,
            Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType()),
            CreatedAt = timeProvider.GetUtcNow(),
        });

        return Task.CompletedTask;
    }
}
