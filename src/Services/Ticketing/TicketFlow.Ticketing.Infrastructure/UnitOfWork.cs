using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Infrastructure;

public sealed class UnitOfWork(TicketingDbContext dbContext) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
