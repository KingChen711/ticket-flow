using MassTransit;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.SalePolicies;

namespace TicketFlow.Reservation.Api.Consumers;

public sealed class CatalogSalePolicyChangedConsumer(
    IEventSalePolicyRepository repository,
    IUnitOfWork unitOfWork) : IConsumer<CatalogSalePolicyChanged>
{
    public async Task Consume(ConsumeContext<CatalogSalePolicyChanged> context)
    {
        var message = context.Message;
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
                     tiers))
        {
            return;
        }

        await unitOfWork.SaveChangesAsync(context.CancellationToken);
    }
}
