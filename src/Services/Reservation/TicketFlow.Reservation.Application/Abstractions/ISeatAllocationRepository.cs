using TicketFlow.Reservation.Domain.Allocations;

namespace TicketFlow.Reservation.Application.Abstractions;

public interface ISeatAllocationRepository
{
    Task<IReadOnlyList<SeatAllocation>> GetBySeatIdsAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeatAllocation>> GetByHoldAsync(
        Guid holdId,
        CancellationToken cancellationToken = default);

    void AddRange(IEnumerable<SeatAllocation> allocations);
}
