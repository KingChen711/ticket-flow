using StackExchange.Redis;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>Full implementation as of Commit 6 — see docs/phase-2-plan.md for the design notes.</summary>
public sealed class RedisSeatHoldStore(IConnectionMultiplexer redis) : ISeatHoldStore
{
    private IDatabase Db => redis.GetDatabase();

    public async Task LoadInventoryAsync(Guid eventId, IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return;

        var values = seatIds.Select(id => (RedisValue)id.ToString()).ToArray();
        await Db.SetAddAsync(RedisKeys.SeatsSet(eventId), values);
    }

    public async Task<IReadOnlyList<Guid>> TryHoldAsync(Guid eventId, IReadOnlyList<Guid> seatIds, Guid holdId,
        TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return [];

        var keys = new RedisKey[3 + seatIds.Count];
        keys[0] = RedisKeys.SeatsSet(eventId);
        keys[1] = RedisKeys.SoldSet(eventId);
        keys[2] = RedisKeys.HoldSeatsSet(holdId);
        for (var i = 0; i < seatIds.Count; i++)
            keys[i + 3] = RedisKeys.Hold(eventId, seatIds[i]);

        var values = new RedisValue[2 + seatIds.Count];
        values[0] = holdId.ToString();
        values[1] = (long)ttl.TotalSeconds;
        for (var i = 0; i < seatIds.Count; i++)
            values[i + 2] = seatIds[i].ToString();

        var result = await Db.ScriptEvaluateAsync(HoldSeatsScript.Source, keys, values);
        var unavailable = (RedisValue[]?)result ?? [];

        return unavailable.Select(seatId => Guid.Parse((string)seatId!)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ReleaseAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default)
    {
        RedisKey[] keys = [RedisKeys.HoldSeatsSet(holdId)];
        RedisValue[] values = [eventId.ToString(), holdId.ToString()];

        var result = await Db.ScriptEvaluateAsync(ReleaseHoldScript.Source, keys, values);
        var released = (RedisValue[]?)result ?? [];

        return released.Select(seatId => Guid.Parse((string)seatId!)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ConfirmAsync(Guid eventId, Guid holdId,
        CancellationToken cancellationToken = default)
    {
        RedisKey[] keys = [RedisKeys.HoldSeatsSet(holdId), RedisKeys.SoldSet(eventId)];
        RedisValue[] values = [eventId.ToString(), holdId.ToString()];

        var result = await Db.ScriptEvaluateAsync(ConfirmHoldScript.Source, keys, values);
        var confirmed = (RedisValue[]?)result ?? [];

        return confirmed.Select(seatId => Guid.Parse((string)seatId!)).ToList();
    }

    public async Task RemoveFromSoldAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return;

        var values = seatIds.Select(id => (RedisValue)id.ToString()).ToArray();
        await Db.SetRemoveAsync(RedisKeys.SoldSet(eventId), values);
    }

    public async Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(Guid eventId,
        IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, SeatStatus>(seatIds.Count);
        if (seatIds.Count == 0)
            return result;

        var db = Db;
        var soldValues = seatIds.Select(id => (RedisValue)id.ToString()).ToArray();

        // Bulk sold check in one round trip; hold-key existence has no equivalent bulk command,
        // but StackExchange.Redis pipelines these concurrent calls over the same multiplexed
        // connection, so this is still one network round trip in practice, not N.
        var soldFlagsTask = db.SetContainsAsync(RedisKeys.SoldSet(eventId), soldValues);
        var heldTasks = seatIds.Select(id => db.KeyExistsAsync(RedisKeys.Hold(eventId, id))).ToArray();

        var soldFlags = await soldFlagsTask;
        await Task.WhenAll(heldTasks);

        for (var i = 0; i < seatIds.Count; i++)
        {
            var status = soldFlags[i]
                ? SeatStatus.Sold
                : heldTasks[i].Result
                    ? SeatStatus.Held
                    : SeatStatus.Available;
            result[seatIds[i]] = status;
        }

        return result;
    }
}
