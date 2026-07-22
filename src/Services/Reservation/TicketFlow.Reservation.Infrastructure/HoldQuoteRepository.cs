using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Holds;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class HoldQuoteRepository(ReservationDbContext dbContext) : IHoldQuoteRepository
{
    public Task<HoldQuote?> GetAsync(Guid holdId, CancellationToken cancellationToken = default) =>
        dbContext.HoldQuotes.SingleOrDefaultAsync(quote => quote.Id == holdId, cancellationToken);

    public Task<HoldQuote?> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        dbContext.HoldQuotes.SingleOrDefaultAsync(quote => quote.OrderId == orderId, cancellationToken);

    public async Task<IReadOnlyList<HoldQuote>> GetActiveOverlappingAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var requestedSeatIds = seatIds.ToArray();
        return await dbContext.HoldQuotes
            .AsNoTracking()
            .Where(quote =>
                quote.EventId == eventId &&
                quote.State == HoldQuoteState.Active &&
                quote.ExpiresAt > now &&
                quote.SeatIds.Any(seatId => requestedSeatIds.Contains(seatId)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveIdsByEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        await dbContext.HoldQuotes
            .AsNoTracking()
            .Where(quote => quote.EventId == eventId && quote.State == HoldQuoteState.Active)
            .Select(quote => quote.Id)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

    public void Add(HoldQuote quote) => dbContext.HoldQuotes.Add(quote);
}
