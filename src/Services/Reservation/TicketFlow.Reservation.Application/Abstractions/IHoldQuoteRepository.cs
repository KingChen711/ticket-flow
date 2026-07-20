using TicketFlow.Reservation.Domain.Holds;

namespace TicketFlow.Reservation.Application.Abstractions;

public interface IHoldQuoteRepository
{
    Task<HoldQuote?> GetAsync(Guid holdId, CancellationToken cancellationToken = default);
    void Add(HoldQuote quote);
}
