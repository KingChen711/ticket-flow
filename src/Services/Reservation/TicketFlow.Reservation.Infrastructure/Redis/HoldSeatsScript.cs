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
    public const string Source = """
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
}
