using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace TicketFlow.Analytics.Api.Messaging;

/// <summary>
/// Analytics is the first Kafka CONSUMER in this codebase (Ordering, Commit 6, is producer-only).
/// Confirmed via a real probe: a TopicEndpoint subscribing to a topic that doesn't exist yet
/// crashes the whole bus at startup — no auto-create-on-subscribe. Must create the topic
/// administratively before AddTicketFlowMessaging's rider starts, same spirit as
/// ElasticsearchIndexSetup/ClickHouseTableSetup elsewhere in Phase 6. Idempotent — tolerates the
/// topic already existing (e.g. Ordering already auto-created it via its first Produce()).
/// </summary>
public static class KafkaTopicSetup
{
    public static async Task EnsureCreatedAsync(string bootstrapServers, string topicName, CancellationToken cancellationToken = default)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topicName, NumPartitions = 1, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Fine — idempotent.
        }
    }
}
