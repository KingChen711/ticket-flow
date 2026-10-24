using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TicketFlow.Reservation.Domain.Seats;
using TicketFlow.Reservation.Infrastructure.Redis;

namespace TicketFlow.Reservation.Infrastructure;

/// <summary>
/// Idempotent compensation for an onboarding workflow that never published. It deliberately
/// refuses to erase any inventory that has acquired a hold, allocation or sold seat.
/// </summary>
public sealed class InventoryCleanupService(
    ReservationDbContext dbContext,
    IConnectionMultiplexer redis)
{
    public async Task CleanupAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var seats = await dbContext.Seats.Where(seat => seat.EventId == eventId).ToListAsync(cancellationToken);
        var hasAllocation = await dbContext.SeatAllocations.AnyAsync(
            allocation => allocation.EventId == eventId,
            cancellationToken);
        var hasQuote = await dbContext.HoldQuotes.AnyAsync(quote => quote.EventId == eventId, cancellationToken);

        if (hasAllocation || hasQuote || seats.Any(seat => seat.Status != SeatStatus.Available))
        {
            throw new InvalidOperationException(
                $"Inventory for event '{eventId}' has already been used and cannot be onboarding-cleaned.");
        }

        var database = redis.GetDatabase();
        var cachedSeatIds = (await database.SetMembersAsync(RedisKeys.SeatsSet(eventId)))
            .Select(value => Guid.TryParse(value.ToString(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty);
        var seatIds = seats.Select(seat => seat.Id).Concat(cachedSeatIds).Distinct().ToList();
        var keys = new List<RedisKey>
        {
            RedisKeys.SeatsSet(eventId),
            RedisKeys.SoldSet(eventId),
            RedisKeys.QuarantinedSet(eventId),
            RedisKeys.RebuildVersion(eventId),
            RedisKeys.RebuildLock(eventId),
        };
        keys.AddRange(seatIds.Select(seatId => (RedisKey)RedisKeys.Hold(eventId, seatId)));
        keys.AddRange(seatIds.Select(seatId => (RedisKey)RedisKeys.Allocation(eventId, seatId)));

        if (keys.Count > 0)
            await database.KeyDeleteAsync(keys.ToArray());

        if (seats.Count > 0)
        {
            dbContext.Seats.RemoveRange(seats);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
