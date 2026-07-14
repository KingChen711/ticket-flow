using TicketFlow.Payment.Application.Abstractions;

namespace TicketFlow.Payment.Infrastructure;

public sealed class UnitOfWork(PaymentDbContext dbContext) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
