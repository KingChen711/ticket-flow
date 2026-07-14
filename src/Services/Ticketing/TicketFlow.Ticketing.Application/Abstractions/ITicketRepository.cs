using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Application.Abstractions;

public interface ITicketRepository
{
    Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default);

    void Add(Ticket ticket);
}
