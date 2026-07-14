using MassTransit;
using TicketFlow.Catalog.Application.Abstractions;

namespace TicketFlow.Catalog.Infrastructure.Messaging;

/// <summary>
/// IPublishEndpoint resolved from the same DI scope as CatalogDbContext is automatically
/// outbox-aware once AddEntityFrameworkOutbox/UseBusOutbox is configured (Commit 3) — Publish()
/// buffers the message against that DbContext instance instead of hitting RabbitMQ directly, and
/// the subsequent SaveChangesAsync flushes it into outbox_message as part of the same
/// transaction. No special outbox-specific API needed here, just the regular IPublishEndpoint.
/// </summary>
public sealed class MassTransitIntegrationEventPublisher(IPublishEndpoint publishEndpoint) : IIntegrationEventPublisher
{
    public Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default) where T : class =>
        publishEndpoint.Publish(integrationEvent, cancellationToken);
}
