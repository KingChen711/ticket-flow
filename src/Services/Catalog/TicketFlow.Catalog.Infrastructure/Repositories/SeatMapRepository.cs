using Microsoft.EntityFrameworkCore;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.SeatMaps;

namespace TicketFlow.Catalog.Infrastructure.Repositories;

public sealed class SeatMapRepository(CatalogDbContext dbContext) : ISeatMapRepository
{
    public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SeatMaps.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<SeatMap?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        dbContext.SeatMaps.FirstOrDefaultAsync(s => s.EventId == eventId, cancellationToken);

    public void Add(SeatMap seatMap) => dbContext.SeatMaps.Add(seatMap);

    public void Remove(SeatMap seatMap) => dbContext.SeatMaps.Remove(seatMap);
}
