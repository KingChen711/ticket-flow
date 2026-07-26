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

    public Task<Ticket?> GetByQrPayloadAsync(string qrPayload, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.FirstOrDefaultAsync(t => t.QrPayload == qrPayload, cancellationToken);

    /// <summary>
    /// <c>ExecuteUpdateAsync</c> issues one UPDATE ... WHERE straight to Postgres and returns the
    /// number of rows it actually changed. That row count IS the answer: 1 means this caller won
    /// the check-in, 0 means someone or something else already made the ticket ineligible. Loading
    /// the entity and letting the change tracker save it would reintroduce the read-then-write gap
    /// this method exists to close.
    /// </summary>
    public async Task<bool> TryClaimCheckInAsync(
        Guid ticketId,
        Guid expectedEventId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var claimed = await dbContext.Tickets
            .Where(t => t.Id == ticketId)
            .Where(t => t.CheckedInAt == null)
            .Where(t => t.RevokedAt == null)
            // A legacy ticket with no EventId cannot prove it belongs at this gate. The domain
            // fast path returns WrongEvent and this database guard must make the same decision.
            .Where(t => t.EventId == expectedEventId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.CheckedInAt, now), cancellationToken);

        return claimed == 1;
    }

    public void Add(Ticket ticket) => dbContext.Tickets.Add(ticket);
}
