using MassTransit;
using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Infrastructure.Messaging;

/// <summary>Same shape as Catalog's implementation — no Outbox here (see PlaceOrderCommandHandler's
/// doc comment: MassTransit.Marten has no Marten-backed Outbox equivalent), so this Publish hits
/// RabbitMQ directly rather than buffering against a DbContext/session.</summary>
public sealed class MassTransitIntegrationEventPublisher(IPublishEndpoint publishEndpoint) : IIntegrationEventPublisher
{
    public Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default) where T : class =>
        publishEndpoint.Publish(integrationEvent, cancellationToken);
}
