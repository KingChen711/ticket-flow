using TicketFlow.Reservation.Application.Abstractions;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class UnitOfWork(ReservationDbContext dbContext) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
