using Microsoft.EntityFrameworkCore;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Events;

namespace TicketFlow.Catalog.Infrastructure.Repositories;

public sealed class EventRepository(CatalogDbContext dbContext) : IEventRepository
{
    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Events.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Event>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Events.AsNoTracking().ToListAsync(cancellationToken);

    public void Add(Event @event) => dbContext.Events.Add(@event);

    public void Remove(Event @event) => dbContext.Events.Remove(@event);
}
