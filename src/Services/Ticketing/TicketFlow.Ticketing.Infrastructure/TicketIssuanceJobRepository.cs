using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure;

public sealed class TicketIssuanceJobRepository(TicketingDbContext dbContext) : ITicketIssuanceJobRepository
{
    public Task<TicketIssuanceJob?> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        dbContext.IssuanceJobs.SingleOrDefaultAsync(job => job.OrderId == orderId, cancellationToken);

    public void Add(TicketIssuanceJob job) => dbContext.IssuanceJobs.Add(job);

    public bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        }

        return false;
    }
}
