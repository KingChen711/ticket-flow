using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;

namespace TicketFlow.Search.Api.Consumers;

/// <summary>
/// Phase 3 stub — logs intent only, no real Elasticsearch index write (that's Phase 6). Together
/// with notification's consumer of the same message, this is what proves pub/sub fan-out: one
/// publish, two independent consumers, each on its own queue bound to the same exchange.
/// </summary>
public sealed class CatalogEventPublishedConsumer(ILogger<CatalogEventPublishedConsumer> logger) : IConsumer<CatalogEventPublished>
{
    public Task Consume(ConsumeContext<CatalogEventPublished> context)
    {
        var message = context.Message;
        logger.LogInformation(
            "[search] sẽ index event {Title} ({CatalogEventId})",
            message.Title,
            message.CatalogEventId);

        return Task.CompletedTask;
    }
}
