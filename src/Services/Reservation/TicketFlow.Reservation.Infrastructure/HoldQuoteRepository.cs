using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Holds;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class HoldQuoteRepository(ReservationDbContext dbContext) : IHoldQuoteRepository
{
    public Task<HoldQuote?> GetAsync(Guid holdId, CancellationToken cancellationToken = default) =>
        dbContext.HoldQuotes.AsNoTracking().SingleOrDefaultAsync(quote => quote.Id == holdId, cancellationToken);

    public void Add(HoldQuote quote) => dbContext.HoldQuotes.Add(quote);
}
