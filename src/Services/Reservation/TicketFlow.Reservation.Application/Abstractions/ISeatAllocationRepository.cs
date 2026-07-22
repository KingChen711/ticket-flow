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

    /// <summary>Every allocation an order owns, whatever its current state — compensation must
    /// see terminal rows too so a redelivery can prove it already ran.</summary>
    Task<IReadOnlyList<SeatAllocation>> GetByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);

    void AddRange(IEnumerable<SeatAllocation> allocations);
}
