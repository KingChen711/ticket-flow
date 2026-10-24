using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Api.Consumers;

/// <summary>
/// Keeps the event → organizer projection Ticketing authorizes gate scans against
/// (Phase 12 Commit 14).
/// </summary>
/// <remarks>
/// Revision-guarded and idempotent, the same shape Search and Notification use for this event
/// (Commit 11): a redelivery or an out-of-order message must not move ownership backwards.
/// A message with no OrganizerId is skipped rather than stored as "unowned" — events published
/// before Commit 14 carry none, and writing a row with an empty owner would be indistinguishable
/// from a real owner nobody matches.
/// </remarks>
public sealed class CatalogEventPublishedConsumer(
    IEventOwnerRepository owners,
    ILogger<CatalogEventPublishedConsumer> logger) : IConsumer<CatalogEventPublished>
{
    public async Task Consume(ConsumeContext<CatalogEventPublished> context)
    {
        var message = context.Message;
        if (message.OrganizerId is not { } organizerId || organizerId == Guid.Empty)
        {
            logger.LogInformation(
                "[ticketing] event {CatalogEventId} published without an organizer; gate scans for it stay admin-only",
                message.CatalogEventId);
            return;
        }

        var occurredAt = message.OccurredAt ?? message.PublishedAt;
        var existing = await owners.GetAsync(message.CatalogEventId, context.CancellationToken);
        if (existing is null)
        {
            owners.Add(EventOwner.Create(
                message.CatalogEventId, organizerId, message.AggregateVersion, occurredAt));
        }
        else if (!existing.TryApply(organizerId, message.AggregateVersion, occurredAt))
        {
            return;
        }

        await owners.SaveChangesAsync(context.CancellationToken);
    }
}
