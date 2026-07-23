using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Contracts.Messaging.Workflows;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.SalePolicies;

namespace TicketFlow.Reservation.Api.Consumers;

public sealed class CatalogSalePolicyChangedConsumer(
    IEventSalePolicyRepository repository,
    IHoldQuoteRepository holdQuoteRepository,
    ISeatHoldStore holdStore,
    IReservationOperationReceiptRepository receiptRepository,
    ISeatAvailabilityEventPublisher availabilityEventPublisher,
    TimeProvider timeProvider,
    IUnitOfWork unitOfWork) : IConsumer<CatalogSalePolicyChanged>
{
    public async Task Consume(ConsumeContext<CatalogSalePolicyChanged> context)
    {
        var message = context.Message;
        await using var salePolicyLock = await receiptRepository.AcquireLockAsync(
            "sale-policy",
            message.EventId.ToString("N"),
            context.CancellationToken);
        var tiers = message.PriceTiers
            .Select(tier => (tier.SectionId, tier.Amount, tier.Currency))
            .ToList();

        var policy = await repository.GetAsync(message.EventId, context.CancellationToken);
        if (policy is null)
        {
            policy = EventSalePolicy.FromSnapshot(
                message.EventId,
                message.Status,
                message.SaleStartsAt,
                message.SaleEndsAt,
                message.PriceSnapshotVersion,
                message.OccurredAt,
                tiers);
            repository.Add(policy);
        }
        else if (!policy.ApplySnapshot(
                     message.Status,
                     message.SaleStartsAt,
                     message.SaleEndsAt,
                     message.PriceSnapshotVersion,
                     message.OccurredAt,
                     tiers) &&
                 (message.CancellationWorkflowId is null ||
                  string.Equals(policy.Status, "Published", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (!string.Equals(message.Status, "Published", StringComparison.OrdinalIgnoreCase))
        {
            var now = timeProvider.GetUtcNow();
            var activeHoldIds = await holdQuoteRepository.GetActiveIdsByEventAsync(
                message.EventId,
                context.CancellationToken);
            foreach (var holdId in activeHoldIds)
            {
                await using var authorityLock = await receiptRepository.AcquireLockAsync(
                    "confirm-hold",
                    holdId.ToString("N"),
                    context.CancellationToken);
                var quote = await holdQuoteRepository.GetAsync(holdId, context.CancellationToken);
                if (quote is null || quote.State != TicketFlow.Reservation.Domain.Holds.HoldQuoteState.Active)
                    continue;

                var release = await holdStore.ReleaseAsync(
                    new ReleaseHoldStoreRequest(
                        quote.EventId,
                        quote.Id,
                        quote.OwnerUserId,
                        quote.Generation,
                        quote.SeatIds),
                    context.CancellationToken);
                if (!release.IsSuccessful)
                {
                    throw new InvalidOperationException(
                        $"Could not invalidate hold '{quote.Id}' after sale-policy change: " +
                        (release.Reason ?? "cache authority conflict"));
                }

                if (quote.ExpiresAt <= now)
                    quote.Expire(now);
                else
                    quote.Invalidate(now);

                await availabilityEventPublisher.PublishAsync(
                    quote.EventId,
                    quote.SeatIds,
                    "Available",
                    quote.Generation,
                    now,
                    context.CancellationToken);
                // This remains inside the EF consumer-outbox transaction in production. Keeping
                // the save inside the authority lock also makes direct consumer tests race-safe.
                await unitOfWork.SaveChangesAsync(context.CancellationToken);
            }
        }

        if (message.CancellationWorkflowId is { } workflowId)
        {
            await context.Publish(new EventCancellationRequested(
                workflowId,
                message.EventId,
                message.CancellationReason ?? "Event cancelled by organizer.",
                message.OccurredAt));
        }

        await unitOfWork.SaveChangesAsync(context.CancellationToken);
    }
}
