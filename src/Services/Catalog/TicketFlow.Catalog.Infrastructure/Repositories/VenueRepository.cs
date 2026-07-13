using Microsoft.EntityFrameworkCore;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Venues;

namespace TicketFlow.Catalog.Infrastructure.Repositories;

public sealed class VenueRepository(CatalogDbContext dbContext) : IVenueRepository
{
    public Task<Venue?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Venues.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Venue>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Venues.AsNoTracking().ToListAsync(cancellationToken);

    public void Add(Venue venue) => dbContext.Venues.Add(venue);
}
