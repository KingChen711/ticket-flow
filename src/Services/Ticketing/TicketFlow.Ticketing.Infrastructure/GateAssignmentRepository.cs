using Microsoft.EntityFrameworkCore;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure;

public sealed class GateAssignmentRepository(TicketingDbContext dbContext) : IGateAssignmentRepository
{
    public Task<GateAssignment?> GetAsync(
        Guid eventId,
        string gateId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedGateId = GateAssignment.NormalizeGateId(gateId);
        return dbContext.GateAssignments.FirstOrDefaultAsync(
            assignment =>
                assignment.EventId == eventId &&
                assignment.GateId == normalizedGateId &&
                assignment.UserId == userId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<GateAssignment>> ListAsync(
        Guid eventId,
        string gateId,
        CancellationToken cancellationToken = default)
    {
        var normalizedGateId = GateAssignment.NormalizeGateId(gateId);
        return await dbContext.GateAssignments
            .AsNoTracking()
            .Where(assignment =>
                assignment.EventId == eventId &&
                assignment.GateId == normalizedGateId)
            .OrderBy(assignment => assignment.AssignedAt)
            .ToListAsync(cancellationToken);
    }

    public void Add(GateAssignment assignment) => dbContext.GateAssignments.Add(assignment);

    public void Remove(GateAssignment assignment) => dbContext.GateAssignments.Remove(assignment);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
