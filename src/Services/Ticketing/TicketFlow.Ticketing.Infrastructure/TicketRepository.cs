using Microsoft.EntityFrameworkCore;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure;

public sealed class TicketRepository(TicketingDbContext dbContext) : ITicketRepository
{
    public async Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        await dbContext.Tickets.Where(t => t.OrderId == orderId).ToListAsync(cancellationToken);

    public Task<Ticket?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    // Tracked (not AsNoTracking) — the validate endpoint calls CheckIn() on the result and
    // expects SaveChangesAsync to persist it.
    public Task<Ticket?> GetByQrPayloadAsync(string qrPayload, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.FirstOrDefaultAsync(t => t.QrPayload == qrPayload, cancellationToken);

    public void Add(Ticket ticket) => dbContext.Tickets.Add(ticket);
}
