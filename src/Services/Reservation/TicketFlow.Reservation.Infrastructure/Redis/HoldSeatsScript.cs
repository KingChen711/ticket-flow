namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// The atomic all-or-nothing hold (Commit 5, the core of Phase 2 — see docs/phase-2-plan.md).
/// Redis executes a Lua script single-threaded and to completion before touching any other
/// command, so the "check every seat, then set every seat" sequence below can never be
/// interleaved with another client's script — that's what makes exactly one concurrent caller
/// win any given seat, without a separate mutex (RedLock) serializing the whole event.
/// </summary>
internal static class HoldSeatsScript
{
    /// <remarks>
    /// KEYS[1]        = seats set key (valid seat ids for the event)
    /// KEYS[2]        = sold set key
    /// KEYS[3]        = holdseats:{holdId} reverse-index set (Commit 6 needs this to find which
    ///                  seats a holdId covers when it only has eventId+holdId to work with)
    /// KEYS[4..N+3]   = one hold key per requested seat
    /// ARGV[1]        = holdId
    /// ARGV[2]        = ttl in seconds
    /// ARGV[3..N+2]   = seat id for each requested seat, parallel to KEYS[4..N+3]
    ///
    /// Returns an empty array on success (every seat now held under holdId), or the list of
    /// seat ids that were NOT available — in which case NOTHING was set, by design.
    /// </remarks>
    public const string LegacySource = """
        local seats_key = KEYS[1]
        local sold_key = KEYS[2]
        local hold_seats_key = KEYS[3]
        local hold_id = ARGV[1]
        local ttl_seconds = ARGV[2]
        local seat_count = #KEYS - 3

        local unavailable = {}

        for i = 1, seat_count do
            local hold_key = KEYS[i + 3]
            local seat_id = ARGV[i + 2]

            local is_valid_seat = redis.call('SISMEMBER', seats_key, seat_id)
            local is_sold = redis.call('SISMEMBER', sold_key, seat_id)
            local is_held = redis.call('EXISTS', hold_key)

            if is_valid_seat == 0 or is_sold == 1 or is_held == 1 then
                table.insert(unavailable, seat_id)
            end
        end

        if #unavailable > 0 then
            return unavailable
        end

        for i = 1, seat_count do
            local hold_key = KEYS[i + 3]
            local seat_id = ARGV[i + 2]
            redis.call('SET', hold_key, hold_id, 'EX', ttl_seconds)
            redis.call('SADD', hold_seats_key, seat_id)
        end
        redis.call('EXPIRE', hold_seats_key, ttl_seconds)

        return {}
        """;

    /// <summary>
    /// Phase 12 typed hold. The value stored per seat is an ownership token rather than a bare
    /// hold id. A retry with the exact same tuple is idempotent; a partial/mismatched reverse
    /// index is a conflict and is never repaired by silently choosing one side.
    /// </summary>
    public const string Source = """
        local seats_key = KEYS[1]
        local sold_key = KEYS[2]
        local hold_seats_key = KEYS[3]
        local hold_id = ARGV[1]
        local owner_id = ARGV[2]
        local generation = ARGV[3]
        local ttl_seconds = tonumber(ARGV[4])
        local seat_count = #KEYS - 3
        local expected = 'H|' .. hold_id .. '|' .. owner_id .. '|' .. generation

        local unavailable = {}
        local conflicts = {}
        local reverse_count = redis.call('SCARD', hold_seats_key)

        if reverse_count > 0 and reverse_count ~= seat_count then
            for i = 1, seat_count do
                table.insert(conflicts, ARGV[i + 4])
            end
            local result = {'CONFLICT'}
            for _, seat_id in ipairs(conflicts) do table.insert(result, seat_id) end
            return result
        end

        if reverse_count > 0 then
            for i = 1, seat_count do
                local seat_id = ARGV[i + 4]
                if redis.call('SISMEMBER', hold_seats_key, seat_id) == 0 then
                    table.insert(conflicts, seat_id)
                end
            end
        end

        for i = 1, seat_count do
            local hold_key = KEYS[i + 3]
            local seat_id = ARGV[i + 4]
            local current = redis.call('GET', hold_key)

            if redis.call('SISMEMBER', seats_key, seat_id) == 0
                or redis.call('SISMEMBER', sold_key, seat_id) == 1 then
                table.insert(unavailable, seat_id)
            elseif current then
                if current == expected then
                    -- Exact retry. The authoritative request supplies the remaining TTL, so
                    -- refreshing to that value cannot extend the immutable quote expiry.
                elseif string.sub(current, 1, string.len('H|' .. hold_id .. '|')) == 'H|' .. hold_id .. '|' then
                    table.insert(conflicts, seat_id)
                else
                    table.insert(unavailable, seat_id)
                end
            end
        end

        if #conflicts > 0 then
            local result = {'CONFLICT'}
            for _, seat_id in ipairs(conflicts) do table.insert(result, seat_id) end
            return result
        end

        if #unavailable > 0 then
            local result = {'UNAVAILABLE'}
            for _, seat_id in ipairs(unavailable) do table.insert(result, seat_id) end
            return result
        end

        if ttl_seconds < 1 then ttl_seconds = 1 end
        for i = 1, seat_count do
            local hold_key = KEYS[i + 3]
            local seat_id = ARGV[i + 4]
            redis.call('SET', hold_key, expected, 'EX', ttl_seconds)
            redis.call('SADD', hold_seats_key, seat_id)
        end
        redis.call('EXPIRE', hold_seats_key, ttl_seconds)

        return {'HELD'}
        """;
}
