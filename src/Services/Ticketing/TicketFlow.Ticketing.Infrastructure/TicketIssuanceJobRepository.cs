using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure;

public sealed class TicketIssuanceJobRepository(TicketingDbContext dbContext) : ITicketIssuanceJobRepository
{
    public Task<TicketIssuanceJob?> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        dbContext.IssuanceJobs.SingleOrDefaultAsync(job => job.OrderId == orderId, cancellationToken);

    public async Task<IReadOnlyList<TicketIssuanceJob>> ListRecoverableAsync(
        DateTimeOffset attemptCutoff,
        DateTimeOffset recoveryCutoff,
        int take,
        CancellationToken cancellationToken = default) =>
        await dbContext.IssuanceJobs
            .Where(job =>
                job.State == TicketIssuanceJobState.Pending &&
                job.LastAttemptAt <= attemptCutoff &&
                (job.LastRecoveryAt == null || job.LastRecoveryAt <= recoveryCutoff) &&
                job.ExpectedSeatIds.Length > 0 &&
                job.CustomerName != null &&
                job.CustomerEmail != null)
            .OrderBy(job => job.LastRecoveryAt ?? job.LastAttemptAt)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TicketIssuanceJob>> ListTerminalArtifactsAsync(
        DateTimeOffset completionCutoff,
        int take,
        CancellationToken cancellationToken = default) =>
        await dbContext.IssuanceJobs
            .Where(job =>
                (job.State == TicketIssuanceJobState.Failed ||
                 job.State == TicketIssuanceJobState.Cancelled) &&
                job.CompletedAt != null &&
                job.CompletedAt <= completionCutoff &&
                job.ArtifactsReconciledAt == null)
            .OrderBy(job => job.CompletedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

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
