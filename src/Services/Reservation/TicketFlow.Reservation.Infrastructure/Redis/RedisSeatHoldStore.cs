using StackExchange.Redis;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Redis acceleration layer. Typed Phase 12 operations carry owner/generation and preserve an
/// exact confirmation marker; legacy overloads remain during the expand/cutover window.
/// </summary>
public sealed class RedisSeatHoldStore(IConnectionMultiplexer redis) : ISeatHoldStore
{
    private IDatabase Db => redis.GetDatabase();

    public async Task LoadInventoryAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return;

        var values = seatIds.Select(id => (RedisValue)id.ToString()).ToArray();
        await Db.SetAddAsync(RedisKeys.SeatsSet(eventId), values);
    }

    public async Task<IReadOnlyList<Guid>> TryHoldAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        Guid holdId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
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
        values[1] = TtlSeconds(ttl);
        for (var i = 0; i < seatIds.Count; i++)
            values[i + 2] = seatIds[i].ToString();

        var result = await Db.ScriptEvaluateAsync(HoldSeatsScript.LegacySource, keys, values);
        return ParseGuids((RedisValue[]?)result ?? []);
    }

    public async Task<HoldStoreResult> TryHoldAsync(
        HoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SeatIds.Count == 0 || request.SeatIds.Distinct().Count() != request.SeatIds.Count)
            return new HoldStoreResult(HoldStoreOutcome.Conflict, request.SeatIds);

        var keys = new RedisKey[3 + request.SeatIds.Count];
        keys[0] = RedisKeys.SeatsSet(request.EventId);
        keys[1] = RedisKeys.SoldSet(request.EventId);
        keys[2] = RedisKeys.HoldSeatsSet(request.HoldId);
        for (var i = 0; i < request.SeatIds.Count; i++)
            keys[i + 3] = RedisKeys.Hold(request.EventId, request.SeatIds[i]);

        var values = new RedisValue[4 + request.SeatIds.Count];
        values[0] = request.HoldId.ToString();
        values[1] = request.OwnerUserId.ToString();
        values[2] = request.Generation;
        values[3] = TtlSeconds(request.Ttl);
        for (var i = 0; i < request.SeatIds.Count; i++)
            values[i + 4] = request.SeatIds[i].ToString();

        var raw = (RedisValue[]?)await Db.ScriptEvaluateAsync(HoldSeatsScript.Source, keys, values) ?? [];
        var status = raw.Length == 0 ? "CONFLICT" : raw[0].ToString();
        var seats = ParseGuids(raw.Skip(1));
        return status switch
        {
            "HELD" => new HoldStoreResult(HoldStoreOutcome.Held, []),
            "UNAVAILABLE" => new HoldStoreResult(HoldStoreOutcome.Unavailable, seats),
            _ => new HoldStoreResult(HoldStoreOutcome.Conflict, seats),
        };
    }

    public async Task<IReadOnlyList<Guid>> ReleaseAsync(
        Guid eventId,
        Guid holdId,
        CancellationToken cancellationToken = default)
    {
        RedisKey[] keys = [RedisKeys.HoldSeatsSet(holdId)];
        RedisValue[] values = [eventId.ToString(), holdId.ToString()];
        var result = await Db.ScriptEvaluateAsync(ReleaseHoldScript.Source, keys, values);
        return ParseGuids((RedisValue[]?)result ?? []);
    }

    public async Task<ReleaseHoldStoreResult> ReleaseAsync(
        ReleaseHoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SeatIds.Count == 0 || request.SeatIds.Distinct().Count() != request.SeatIds.Count)
            return new ReleaseHoldStoreResult(ReleaseHoldStoreOutcome.Conflict, "The exact seat set is invalid.");

        var seats = request.SeatIds.OrderBy(id => id).ToArray();
        var keys = new RedisKey[1 + seats.Length];
        keys[0] = RedisKeys.HoldSeatsSet(request.HoldId);
        for (var i = 0; i < seats.Length; i++)
            keys[i + 1] = RedisKeys.Hold(request.EventId, seats[i]);

        var values = new RedisValue[3 + seats.Length];
        values[0] = request.HoldId.ToString();
        values[1] = request.OwnerUserId.ToString();
        values[2] = request.Generation;
        for (var i = 0; i < seats.Length; i++)
            values[i + 3] = seats[i].ToString();

        var raw = (RedisValue[]?)await Db.ScriptEvaluateAsync(ReleaseHoldScript.TypedSource, keys, values) ?? [];
        var status = raw.Length == 0 ? "CONFLICT" : raw[0].ToString();
        return new ReleaseHoldStoreResult(
            status switch
            {
                "APPLIED" => ReleaseHoldStoreOutcome.Applied,
                "ALREADY_RELEASED" => ReleaseHoldStoreOutcome.AlreadyReleased,
                _ => ReleaseHoldStoreOutcome.Conflict,
            },
            raw.Length > 1 ? raw[1].ToString() : null);
    }

    public async Task<IReadOnlyList<Guid>> ConfirmAsync(
        Guid eventId,
        Guid holdId,
        CancellationToken cancellationToken = default)
    {
        RedisKey[] keys = [RedisKeys.HoldSeatsSet(holdId), RedisKeys.SoldSet(eventId)];
        RedisValue[] values = [eventId.ToString(), holdId.ToString()];
        var result = await Db.ScriptEvaluateAsync(ConfirmHoldScript.LegacySource, keys, values);
        return ParseGuids((RedisValue[]?)result ?? []);
    }

    public async Task<ConfirmHoldStoreResult> ConfirmAsync(
        ConfirmHoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SeatIds.Count == 0 || request.SeatIds.Distinct().Count() != request.SeatIds.Count)
        {
            return new ConfirmHoldStoreResult(
                ConfirmHoldStoreOutcome.Conflict,
                [],
                "The authoritative seat set must be non-empty and contain no duplicates.");
        }

        var seats = request.SeatIds.OrderBy(id => id).ToArray();
        var keys = new RedisKey[5 + (2 * seats.Length)];
        keys[0] = RedisKeys.HoldSeatsSet(request.HoldId);
        keys[1] = RedisKeys.SoldSet(request.EventId);
        keys[2] = RedisKeys.ConfirmedSeatsSet(request.EventId, request.HoldId);
        keys[3] = RedisKeys.ConfirmedMeta(request.EventId, request.HoldId);
        keys[4] = RedisKeys.ConfirmedOrder(request.OrderId);
        for (var i = 0; i < seats.Length; i++)
        {
            keys[i + 5] = RedisKeys.Hold(request.EventId, seats[i]);
            keys[i + 5 + seats.Length] = RedisKeys.Allocation(request.EventId, seats[i]);
        }

        var values = new RedisValue[4 + seats.Length];
        values[0] = request.HoldId.ToString();
        values[1] = request.OrderId.ToString();
        values[2] = request.OwnerUserId.ToString();
        values[3] = request.Generation;
        for (var i = 0; i < seats.Length; i++)
            values[i + 4] = seats[i].ToString();

        var raw = (RedisValue[]?)await Db.ScriptEvaluateAsync(ConfirmHoldScript.Source, keys, values) ?? [];
        var status = raw.Length == 0 ? "CONFLICT" : raw[0].ToString();
        if (status == "APPLIED" || status == "ALREADY_APPLIED_SAME")
        {
            var confirmed = ParseGuids(raw.Skip(1));
            return new ConfirmHoldStoreResult(
                status == "APPLIED"
                    ? ConfirmHoldStoreOutcome.Applied
                    : ConfirmHoldStoreOutcome.AlreadyAppliedSame,
                confirmed);
        }

        return new ConfirmHoldStoreResult(
            status == "MISSING_OR_EXPIRED"
                ? ConfirmHoldStoreOutcome.MissingOrExpired
                : ConfirmHoldStoreOutcome.Conflict,
            [],
            raw.Length > 1 ? raw[1].ToString() : null);
    }

    public async Task<ReleaseAllocationStoreResult> ReleaseAllocationAsync(
        ReleaseAllocationStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SeatIds.Count == 0 || request.SeatIds.Distinct().Count() != request.SeatIds.Count)
        {
            return new ReleaseAllocationStoreResult(
                ReleaseAllocationStoreOutcome.Conflict,
                [],
                [],
                "The exact seat set must be non-empty and contain no duplicates.");
        }

        var seats = request.SeatIds.OrderBy(id => id).ToArray();
        var keys = new RedisKey[4 + seats.Length];
        keys[0] = RedisKeys.SoldSet(request.EventId);
        keys[1] = RedisKeys.ConfirmedSeatsSet(request.EventId, request.HoldId);
        keys[2] = RedisKeys.ConfirmedMeta(request.EventId, request.HoldId);
        keys[3] = RedisKeys.ConfirmedOrder(request.OrderId);
        for (var i = 0; i < seats.Length; i++)
            keys[i + 4] = RedisKeys.Allocation(request.EventId, seats[i]);

        var values = new RedisValue[4 + seats.Length];
        values[0] = request.HoldId.ToString();
        values[1] = request.OrderId.ToString();
        values[2] = request.OwnerUserId.ToString();
        values[3] = request.Generation;
        for (var i = 0; i < seats.Length; i++)
            values[i + 4] = seats[i].ToString();

        var raw = (RedisValue[]?)await Db.ScriptEvaluateAsync(ReleaseAllocationScript.Source, keys, values) ?? [];
        if (raw.Length < 2)
        {
            return new ReleaseAllocationStoreResult(
                ReleaseAllocationStoreOutcome.Conflict, [], [], "The release script returned no status.");
        }

        var status = raw[0].ToString();
        var releasedCount = int.Parse(raw[1].ToString());
        var seatValues = raw.Skip(2).ToArray();
        var released = ParseGuids(seatValues.Take(releasedCount));
        var stale = ParseGuids(seatValues.Skip(releasedCount));

        return new ReleaseAllocationStoreResult(
            status switch
            {
                "APPLIED" => ReleaseAllocationStoreOutcome.Applied,
                "ALREADY_RELEASED" => ReleaseAllocationStoreOutcome.AlreadyReleased,
                "OWNERSHIP_CHANGED" => ReleaseAllocationStoreOutcome.OwnershipChanged,
                _ => ReleaseAllocationStoreOutcome.Conflict,
            },
            released,
            stale,
            stale.Count > 0 ? "One or more seats are owned by a newer allocation." : null);
    }

    public async Task RestoreAsync(
        RestoreSeatAllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        var allocation = request.Allocation;
        if (allocation.State == CachedAllocationState.Held
            && (!allocation.ExpiresAt.HasValue || allocation.ExpiresAt.Value <= DateTimeOffset.UtcNow))
        {
            return;
        }

        if (allocation.State == CachedAllocationState.Confirmed && !allocation.OrderId.HasValue)
            throw new InvalidOperationException("A confirmed cache allocation requires an order id.");

        var state = StateCode(allocation.State);
        var ttl = allocation.ExpiresAt.HasValue
            ? TtlSeconds(allocation.ExpiresAt.Value - DateTimeOffset.UtcNow)
            : 1L;
        RedisKey[] keys =
        [
            RedisKeys.SeatsSet(request.EventId),
            RedisKeys.SoldSet(request.EventId),
            RedisKeys.QuarantinedSet(request.EventId),
            RedisKeys.Hold(request.EventId, allocation.SeatId),
            RedisKeys.HoldSeatsSet(allocation.HoldId),
            RedisKeys.Allocation(request.EventId, allocation.SeatId),
            RedisKeys.ConfirmedSeatsSet(request.EventId, allocation.HoldId),
            RedisKeys.ConfirmedMeta(request.EventId, allocation.HoldId),
            allocation.OrderId.HasValue
                ? RedisKeys.ConfirmedOrder(allocation.OrderId.Value)
                : RedisKeys.ConfirmedOrder(Guid.Empty),
        ];
        RedisValue[] values =
        [
            state,
            allocation.SeatId.ToString(),
            allocation.HoldId.ToString(),
            allocation.OrderId?.ToString() ?? string.Empty,
            allocation.OwnerUserId.ToString(),
            allocation.Generation,
            ttl,
        ];

        var raw = (RedisValue[]?)await Db.ScriptEvaluateAsync(RestoreSeatAllocationScript.Source, keys, values) ?? [];
        if (raw.Length == 0 || raw[0].ToString() != "APPLIED")
        {
            var reason = raw.Length > 1 ? raw[1].ToString() : "unknown restore conflict";
            throw new InvalidOperationException($"Redis allocation restore was rejected: {reason}.");
        }
    }

    public async Task<RebuildSeatCacheResult> RebuildAsync(
        RebuildSeatCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        var quarantinedSeatIds = request.QuarantinedSeatIds ?? [];
        if (request.ValidSeatIds.Distinct().Count() != request.ValidSeatIds.Count
            || request.Allocations.Select(item => item.SeatId).Distinct().Count() != request.Allocations.Count
            || request.Allocations.Any(item => !request.ValidSeatIds.Contains(item.SeatId))
            || quarantinedSeatIds.Distinct().Count() != quarantinedSeatIds.Count
            || quarantinedSeatIds.Any(item => !request.ValidSeatIds.Contains(item))
            || request.Allocations.Any(item => quarantinedSeatIds.Contains(item.SeatId)))
        {
            var conflicts = request.Allocations.Select(item => item.SeatId)
                .Concat(quarantinedSeatIds)
                .Distinct()
                .ToArray();
            return new RebuildSeatCacheResult(false, conflicts, request.SnapshotVersion);
        }

        var lockKey = RedisKeys.RebuildLock(request.EventId);
        var lockToken = Guid.NewGuid().ToString("N");
        var acquired = await Db.StringSetAsync(lockKey, lockToken, TimeSpan.FromMinutes(1), When.NotExists);
        if (!acquired)
            return new RebuildSeatCacheResult(false, [], request.SnapshotVersion);

        try
        {
            RedisKey[] keys =
            [
                RedisKeys.SeatsSet(request.EventId),
                RedisKeys.SoldSet(request.EventId),
                RedisKeys.QuarantinedSet(request.EventId),
                RedisKeys.RebuildVersion(request.EventId),
                lockKey,
            ];

            var now = DateTimeOffset.UtcNow;
            var values = new List<RedisValue>(6 + request.ValidSeatIds.Count + (7 * request.Allocations.Count))
            {
                lockToken,
                request.EventId.ToString(),
                request.SnapshotVersion,
                now.ToUnixTimeMilliseconds(),
                request.ValidSeatIds.Count,
            };
            values.AddRange(request.ValidSeatIds.OrderBy(id => id).Select(id => (RedisValue)id.ToString()));
            values.Add(request.Allocations.Count);
            foreach (var allocation in request.Allocations.OrderBy(item => item.SeatId))
            {
                values.Add(allocation.SeatId.ToString());
                values.Add(StateCode(allocation.State));
                values.Add(allocation.HoldId.ToString());
                values.Add(allocation.OrderId?.ToString() ?? string.Empty);
                values.Add(allocation.OwnerUserId.ToString());
                values.Add(allocation.Generation);
                values.Add(allocation.ExpiresAt?.ToUnixTimeMilliseconds() ?? 0);
            }
            values.Add(quarantinedSeatIds.Count);
            values.AddRange(quarantinedSeatIds.OrderBy(id => id).Select(id => (RedisValue)id.ToString()));

            var raw = (RedisValue[]?)await Db.ScriptEvaluateAsync(RebuildSeatCacheScript.Source, keys, values.ToArray()) ?? [];
            var applied = raw.Length > 0 && raw[0].ToString() == "APPLIED";
            var conflicts = raw.Length > 1 ? ParseGuids(raw.Skip(1)) : [];
            return new RebuildSeatCacheResult(applied, conflicts, request.SnapshotVersion);
        }
        finally
        {
            await Db.ScriptEvaluateAsync(
                RebuildSeatCacheScript.ReleaseLockSource,
                [lockKey],
                [lockToken]);
        }
    }

    public async Task RemoveFromSoldAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return;

        var values = seatIds.Select(id => (RedisValue)id.ToString()).ToArray();
        await Db.SetRemoveAsync(RedisKeys.SoldSet(eventId), values);
    }

    public async Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, SeatStatus>(seatIds.Count);
        if (seatIds.Count == 0)
            return result;

        var values = seatIds.Select(id => (RedisValue)id.ToString()).ToArray();
        var soldFlagsTask = Db.SetContainsAsync(RedisKeys.SoldSet(eventId), values);
        var quarantineFlagsTask = Db.SetContainsAsync(RedisKeys.QuarantinedSet(eventId), values);
        var heldTasks = seatIds.Select(id => Db.KeyExistsAsync(RedisKeys.Hold(eventId, id))).ToArray();

        var soldFlags = await soldFlagsTask;
        var quarantineFlags = await quarantineFlagsTask;
        await Task.WhenAll(heldTasks);

        for (var i = 0; i < seatIds.Count; i++)
        {
            result[seatIds[i]] = soldFlags[i] || quarantineFlags[i]
                ? SeatStatus.Sold
                : heldTasks[i].Result
                    ? SeatStatus.Held
                    : SeatStatus.Available;
        }

        return result;
    }

    private static long TtlSeconds(TimeSpan ttl) => Math.Max(1L, (long)Math.Ceiling(ttl.TotalSeconds));

    private static string StateCode(CachedAllocationState state) => state switch
    {
        CachedAllocationState.Held => "H",
        CachedAllocationState.Confirmed => "S",
        CachedAllocationState.Quarantined => "Q",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown cached allocation state."),
    };

    private static IReadOnlyList<Guid> ParseGuids(IEnumerable<RedisValue> values) =>
        values.Where(value => value.HasValue).Select(value => Guid.Parse(value.ToString())).ToArray();
}
