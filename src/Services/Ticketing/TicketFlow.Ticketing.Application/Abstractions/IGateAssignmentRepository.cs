using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Application.Abstractions;

public interface IGateAssignmentRepository
{
    Task<GateAssignment?> GetAsync(
        Guid eventId,
        string gateId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GateAssignment>> ListAsync(
        Guid eventId,
        string gateId,
        CancellationToken cancellationToken = default);

    void Add(GateAssignment assignment);

    void Remove(GateAssignment assignment);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
