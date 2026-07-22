using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Allocations;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class SeatAllocationRepository(ReservationDbContext dbContext) : ISeatAllocationRepository
{
    public async Task<IReadOnlyList<SeatAllocation>> GetBySeatIdsAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        await dbContext.SeatAllocations
            .Where(allocation => allocation.EventId == eventId && seatIds.Contains(allocation.SeatId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SeatAllocation>> GetByHoldAsync(
        Guid holdId,
        CancellationToken cancellationToken = default) =>
        await dbContext.SeatAllocations
            .Where(allocation => allocation.HoldId == holdId)
            .OrderBy(allocation => allocation.SeatId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SeatAllocation>> GetByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        await dbContext.SeatAllocations
            .Where(allocation => allocation.OrderId == orderId)
            .OrderBy(allocation => allocation.SeatId)
            .ToListAsync(cancellationToken);

    public void AddRange(IEnumerable<SeatAllocation> allocations) =>
        dbContext.SeatAllocations.AddRange(allocations);
}
