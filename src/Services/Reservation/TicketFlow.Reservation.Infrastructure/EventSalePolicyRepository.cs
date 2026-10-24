using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.SalePolicies;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class EventSalePolicyRepository(ReservationDbContext dbContext) : IEventSalePolicyRepository
{
    public Task<EventSalePolicy?> GetAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        dbContext.EventSalePolicies
            .Include(policy => policy.PriceTiers)
            .SingleOrDefaultAsync(policy => policy.Id == eventId, cancellationToken);

    public void Add(EventSalePolicy policy) => dbContext.EventSalePolicies.Add(policy);
}
