using StackExchange.Redis;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// LoadInventoryAsync and TryHoldAsync (Commit 4 + Commit 5, see docs/phase-2-plan.md) are
/// real. Release/Confirm/GetLiveStatuses are still NotImplementedException — Commit 6 fills
/// them in.
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

    public async Task<IReadOnlyList<Guid>> TryHoldAsync(Guid eventId, IReadOnlyList<Guid> seatIds, Guid holdId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return [];

        var keys = new RedisKey[2 + seatIds.Count];
        keys[0] = RedisKeys.SeatsSet(eventId);
        keys[1] = RedisKeys.SoldSet(eventId);
        for (var i = 0; i < seatIds.Count; i++)
            keys[i + 2] = RedisKeys.Hold(eventId, seatIds[i]);

        var values = new RedisValue[2 + seatIds.Count];
        values[0] = holdId.ToString();
        values[1] = (long)ttl.TotalSeconds;
        for (var i = 0; i < seatIds.Count; i++)
            values[i + 2] = seatIds[i].ToString();

        var result = await Db.ScriptEvaluateAsync(HoldSeatsScript.Source, keys, values);
        var unavailable = (RedisValue[]?)result ?? [];

        return unavailable.Select(seatId => Guid.Parse((string)seatId!)).ToList();
    }

    public Task<bool> ReleaseAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Lands in Commit 6 — see docs/phase-2-plan.md.");

    public Task<IReadOnlyList<Guid>> ConfirmAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Lands in Commit 6 — see docs/phase-2-plan.md.");

    public Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Lands in Commit 6 — see docs/phase-2-plan.md.");
}
