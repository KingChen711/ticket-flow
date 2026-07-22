using TicketFlow.Reservation.Domain.Holds;

namespace TicketFlow.Reservation.Application.Abstractions;

public interface IHoldQuoteRepository
{
    Task<HoldQuote?> GetAsync(Guid holdId, CancellationToken cancellationToken = default);

    Task<HoldQuote?> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HoldQuote>> GetActiveOverlappingAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetActiveIdsByEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    void Add(HoldQuote quote);
}
