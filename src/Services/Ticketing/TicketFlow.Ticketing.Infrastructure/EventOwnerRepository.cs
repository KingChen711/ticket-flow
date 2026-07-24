using Microsoft.EntityFrameworkCore;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure;

public sealed class EventOwnerRepository(TicketingDbContext dbContext) : IEventOwnerRepository
{
    public Task<EventOwner?> GetAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        dbContext.Set<EventOwner>().FirstOrDefaultAsync(o => o.EventId == eventId, cancellationToken);

    public void Add(EventOwner owner) => dbContext.Set<EventOwner>().Add(owner);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
