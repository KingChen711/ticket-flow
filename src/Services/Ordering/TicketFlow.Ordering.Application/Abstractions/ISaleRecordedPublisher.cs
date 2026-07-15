using TicketFlow.Contracts.Messaging.Analytics;

namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// Deliberately separate from IIntegrationEventPublisher (RabbitMQ/IPublishEndpoint) — that port
/// exists to keep Application off MassTransit's publish API, but reusing it here would blur the
/// exact "2 transport, 2 mục đích" distinction this port exists to make visible (Phase 6 mục 3):
/// this dependency is Kafka-bound, not RabbitMQ-bound, and the name says so. Application still
/// only knows core MassTransit (MassTransitStateMachine etc.) — MassTransit.Kafka's
/// ITopicProducer&lt;T&gt; stays out, same as MassTransit.RabbitMQ's IRabbitMqBusFactoryConfigurator
/// already does; the real implementation wrapping ITopicProducer lives at the Api composition
/// root, same split as MassTransitIntegrationEventPublisher/MartenRepository.
/// </summary>
public interface ISaleRecordedPublisher
{
    Task PublishAsync(SaleRecorded message, CancellationToken cancellationToken = default);
}
