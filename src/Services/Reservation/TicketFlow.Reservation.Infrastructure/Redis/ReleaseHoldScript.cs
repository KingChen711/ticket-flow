namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Only the exclusive holder of a seat can release it — checks the hold key's value still
/// equals holdId before deleting (guards against releasing a DIFFERENT hold that reused the
/// seat after this one already expired on its own).
/// </summary>
internal static class ReleaseHoldScript
{
    /// <remarks>
    /// KEYS[1]  = holdseats:{holdId} reverse-index set
    /// ARGV[1]  = eventId
    /// ARGV[2]  = holdId
    ///
    /// Per-seat hold keys are built dynamically inside the script from the seat ids read out
    /// of KEYS[1] rather than being declared in KEYS[] up front — not Redis Cluster-safe
    /// (cluster requires every touched key to be pre-declared for hash-slot routing), but this
    /// project runs a single Redis node, so it's an acceptable simplification (see
    /// docs/phase-2-plan.md) that avoids a round trip to fetch the seat list before calling this.
    ///
    /// Returns the seat ids actually released — Phase 8 Commit 6 needs the real list (not just a
    /// count) to broadcast which seats went back to Available, same reasoning as ConfirmHoldScript.
    /// </remarks>
    public const string Source = """
        local hold_seats_key = KEYS[1]
        local event_id = ARGV[1]
        local hold_id = ARGV[2]

        local seat_ids = redis.call('SMEMBERS', hold_seats_key)
        local released = {}

        for _, seat_id in ipairs(seat_ids) do
            local hold_key = 'hold:' .. event_id .. ':' .. seat_id
            local current = redis.call('GET', hold_key)
            local typed_prefix = 'H|' .. hold_id .. '|'
            if current == hold_id
                or (current and string.sub(current, 1, string.len(typed_prefix)) == typed_prefix) then
                redis.call('DEL', hold_key)
                table.insert(released, seat_id)
            end
        end

        redis.call('DEL', hold_seats_key)

        return released
        """;

    /// <summary>
    /// Phase 12 owner/generation-aware exact release. Every key touched is supplied by the
    /// caller, so a stale release can never delete a newer holder's token.
    /// </summary>
    public const string TypedSource = """
        local hold_seats_key = KEYS[1]
        local hold_id = ARGV[1]
        local owner_id = ARGV[2]
        local generation = ARGV[3]
        local seat_count = #KEYS - 1
        local expected = 'H|' .. hold_id .. '|' .. owner_id .. '|' .. generation
        local hold_prefix = 'H|' .. hold_id .. '|'
        local reverse_count = redis.call('SCARD', hold_seats_key)

        if reverse_count > 0 and reverse_count ~= seat_count then
            return {'CONFLICT', 'reverse seat count mismatch'}
        end

        if reverse_count > 0 then
            for i = 1, seat_count do
                if redis.call('SISMEMBER', hold_seats_key, ARGV[i + 3]) == 0 then
                    return {'CONFLICT', 'reverse seat set mismatch'}
                end
            end
        end

        local owned_count = 0
        for i = 1, seat_count do
            local current = redis.call('GET', KEYS[i + 1])
            if current == expected then
                owned_count = owned_count + 1
            elseif current and string.sub(current, 1, string.len(hold_prefix)) == hold_prefix then
                return {'CONFLICT', 'owner or generation mismatch'}
            elseif reverse_count > 0 and current then
                return {'CONFLICT', 'seat is owned by another hold'}
            end
        end

        for i = 1, seat_count do
            if redis.call('GET', KEYS[i + 1]) == expected then
                redis.call('DEL', KEYS[i + 1])
            end
        end
        redis.call('DEL', hold_seats_key)

        if owned_count == 0 then
            return {'ALREADY_RELEASED'}
        end
        return {'APPLIED'}
        """;
}
