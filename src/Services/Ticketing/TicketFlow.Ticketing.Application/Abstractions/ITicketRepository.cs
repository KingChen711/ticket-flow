using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Application.Abstractions;

public interface ITicketRepository
{
    Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<Ticket?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Ticket?> GetByQrPayloadAsync(string qrPayload, CancellationToken cancellationToken = default);

    void Add(Ticket ticket);
}
