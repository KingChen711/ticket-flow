using Microsoft.EntityFrameworkCore;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure;

public sealed class TicketRepository(TicketingDbContext dbContext) : ITicketRepository
{
    public async Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        await dbContext.Tickets.Where(t => t.OrderId == orderId).ToListAsync(cancellationToken);

    public void Add(Ticket ticket) => dbContext.Tickets.Add(ticket);
}
