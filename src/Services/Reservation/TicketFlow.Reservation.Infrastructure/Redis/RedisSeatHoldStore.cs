using StackExchange.Redis;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Skeleton only (Commit 4, see docs/phase-2-plan.md) — proves the Redis connection and lets
/// AddReservationApplication() be wired into DI without failing service-provider validation
/// (same trap as Identity Commit 11). LoadInventoryAsync has no concurrency concerns, so it's
/// implemented for real here. The four methods that ARE the anti-oversell mechanism
/// (TryHoldAsync via atomic Lua, then Release/Confirm/GetLiveStatuses) are still
/// NotImplementedException — Commit 5 and Commit 6 fill them in.
/// </summary>
public sealed class RedisSeatHoldStore(IConnectionMultiplexer redis) : ISeatHoldStore
{
    private IDatabase Db => redis.GetDatabase();

    public async Task LoadInventoryAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return;

        var values = seatIds.Select(id => (RedisValue)id.ToString()).ToArray();
        await Db.SetAddAsync(RedisKeys.SeatsSet(eventId), values);
    }

    public Task<IReadOnlyList<Guid>> TryHoldAsync(Guid eventId, IReadOnlyList<Guid> seatIds, Guid holdId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Atomic Lua hold lands in Commit 5 — see docs/phase-2-plan.md.");

    public Task<bool> ReleaseAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Lands in Commit 6 — see docs/phase-2-plan.md.");

    public Task<IReadOnlyList<Guid>> ConfirmAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Lands in Commit 6 — see docs/phase-2-plan.md.");

    public Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Lands in Commit 6 — see docs/phase-2-plan.md.");
}
