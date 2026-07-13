using TicketFlow.Identity.Application.Abstractions;

namespace TicketFlow.Identity.Infrastructure;

public sealed class UnitOfWork(IdentityDbContext dbContext) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
