using MassTransit;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Api.Messaging;

/// <summary>
/// Wraps MassTransit.Kafka's ITopicProducer&lt;SaleRecorded&gt; — lives at the Api composition
/// root, not Infrastructure, same split as MassTransitIntegrationEventPublisher/MartenRepository
/// (transport wiring stays out of both Application and Infrastructure; see
/// ISaleRecordedPublisher's doc comment for why).
/// </summary>
public sealed class KafkaSaleRecordedPublisher(ITopicProducer<SaleRecorded> topicProducer) : ISaleRecordedPublisher
{
    public Task PublishAsync(SaleRecorded message, CancellationToken cancellationToken = default) =>
        topicProducer.Produce(message, cancellationToken);
}
