using MassTransit;
using TicketFlow.Analytics.Application.Abstractions;
using TicketFlow.Contracts.Messaging.Catalog;

namespace TicketFlow.Analytics.Api.Consumers;

/// <summary>
/// Keeps the event → organizer projection that scopes <c>GET /revenue</c> (Phase 12 Commit 14).
/// </summary>
/// <remarks>
/// This is Analytics' first RabbitMQ consumer — sales arrive over Kafka, but event ownership is a
/// Catalog lifecycle fact that already travels on the bus, and re-publishing it to Kafka purely so
/// Analytics could read it would add a second copy of the same truth. An event published before
/// Commit 14 carries no organizer; skipping it leaves its revenue visible to admins only, which is
/// the correct fail-closed answer for "we do not know whose this is".
/// </remarks>
public sealed class CatalogEventPublishedConsumer(
    ISalesStore salesStore,
    ILogger<CatalogEventPublishedConsumer> logger) : IConsumer<CatalogEventPublished>
{
    public async Task Consume(ConsumeContext<CatalogEventPublished> context)
    {
        var message = context.Message;
        if (message.OrganizerId is not { } organizerId || organizerId == Guid.Empty)
        {
            logger.LogInformation(
                "[analytics] event {CatalogEventId} published without an organizer; its revenue stays admin-only",
                message.CatalogEventId);
            return;
        }

        await salesStore.RecordEventOwnerAsync(
            message.CatalogEventId, organizerId, message.AggregateVersion, context.CancellationToken);
    }
}
