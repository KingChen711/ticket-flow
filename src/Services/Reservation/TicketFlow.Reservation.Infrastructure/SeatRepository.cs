using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class SeatRepository(ReservationDbContext dbContext) : ISeatRepository
{
    public Task<bool> InventoryExistsAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        dbContext.Seats.AnyAsync(s => s.EventId == eventId, cancellationToken);

    public void AddRange(IEnumerable<Seat> seats) => dbContext.Seats.AddRange(seats);

    public async Task<IReadOnlyList<Seat>> GetByEventAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        await dbContext.Seats.AsNoTracking().Where(s => s.EventId == eventId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Seat>> GetByIdsAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default) =>
        await dbContext.Seats.Where(s => s.EventId == eventId && seatIds.Contains(s.Id)).ToListAsync(cancellationToken);
}
