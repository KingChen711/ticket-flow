namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Restores one PostgreSQL-authoritative allocation into Redis. A held/confirmed restore never
/// overwrites a different ownership token. Quarantine is the exception: it only adds fail-closed
/// membership and deliberately preserves both conflicting tokens for diagnosis.
/// </summary>
internal static class RestoreSeatAllocationScript
{
    /// <remarks>
    /// KEYS: valid seats, sold, quarantine, hold key, held reverse set, allocation key,
    ///       confirmed set, confirmed meta, confirmed order claim.
    /// ARGV: state (H/S/Q), seat, hold, order, owner, generation, ttl seconds.
    /// </remarks>
    public const string Source = """
        local valid_key = KEYS[1]
        local sold_key = KEYS[2]
        local quarantine_key = KEYS[3]
        local hold_key = KEYS[4]
        local hold_seats_key = KEYS[5]
        local allocation_key = KEYS[6]
        local confirmed_seats_key = KEYS[7]
        local confirmed_meta_key = KEYS[8]
        local confirmed_order_key = KEYS[9]

        local state = ARGV[1]
        local seat_id = ARGV[2]
        local hold_id = ARGV[3]
        local order_id = ARGV[4]
        local owner_id = ARGV[5]
        local generation = ARGV[6]
        local ttl_seconds = tonumber(ARGV[7])
        local held_token = 'H|' .. hold_id .. '|' .. owner_id .. '|' .. generation
        local confirmed_token = 'S|' .. hold_id .. '|' .. order_id .. '|' .. owner_id .. '|' .. generation
        local order_token = 'O|' .. hold_id .. '|' .. owner_id .. '|' .. generation

        redis.call('SADD', valid_key, seat_id)

        if state == 'Q' then
            redis.call('SADD', quarantine_key, seat_id)
            redis.call('SADD', sold_key, seat_id)
            return {'APPLIED'}
        end

        if redis.call('SISMEMBER', quarantine_key, seat_id) == 1 then
            return {'CONFLICT', 'seat-is-quarantined'}
        end

        local current_hold = redis.call('GET', hold_key)
        local current_allocation = redis.call('GET', allocation_key)

        if state == 'H' then
            if current_hold and current_hold ~= held_token then
                return {'CONFLICT', 'held-token-mismatch'}
            end
            if current_allocation and current_allocation ~= held_token then
                return {'CONFLICT', 'allocation-token-mismatch'}
            end
            if redis.call('SISMEMBER', sold_key, seat_id) == 1 then
                return {'CONFLICT', 'seat-already-sold'}
            end
            if ttl_seconds < 1 then ttl_seconds = 1 end
            redis.call('SET', hold_key, held_token, 'EX', ttl_seconds)
            redis.call('SADD', hold_seats_key, seat_id)
            redis.call('EXPIRE', hold_seats_key, ttl_seconds)
            return {'APPLIED'}
        end

        if state == 'S' then
            local current_order = redis.call('GET', confirmed_order_key)
            if current_order and current_order ~= order_token then
                return {'CONFLICT', 'order-already-confirmed-by-another-hold'}
            end
            if current_allocation and current_allocation ~= confirmed_token then
                return {'CONFLICT', 'confirmed-token-mismatch'}
            end
            if current_hold and current_hold ~= held_token then
                return {'CONFLICT', 'held-predecessor-mismatch'}
            end
            redis.call('DEL', hold_key)
            redis.call('SREM', hold_seats_key, seat_id)
            if redis.call('SCARD', hold_seats_key) == 0 then redis.call('DEL', hold_seats_key) end
            redis.call('SADD', sold_key, seat_id)
            redis.call('SET', allocation_key, confirmed_token)
            redis.call('SET', confirmed_order_key, order_token)
            redis.call('SADD', confirmed_seats_key, seat_id)
            local confirmed_seats = redis.call('SMEMBERS', confirmed_seats_key)
            table.sort(confirmed_seats)
            redis.call('SET', confirmed_meta_key,
                confirmed_token .. '|' .. table.concat(confirmed_seats, ','))
            return {'APPLIED'}
        end

        return {'CONFLICT', 'unknown-state'}
        """;
}
