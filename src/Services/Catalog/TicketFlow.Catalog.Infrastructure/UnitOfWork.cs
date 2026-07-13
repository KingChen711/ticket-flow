using TicketFlow.Catalog.Application.Abstractions;

namespace TicketFlow.Catalog.Infrastructure;

public sealed class UnitOfWork(CatalogDbContext dbContext) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
