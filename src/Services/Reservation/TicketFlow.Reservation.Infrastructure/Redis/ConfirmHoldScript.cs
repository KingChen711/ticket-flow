namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Held → Sold on the Redis side only. The caller (ReservationService.ConfirmAsync) still has
/// to persist the same seats as Sold in Postgres afterward — the two are NOT atomic across
/// stores (that gap is Saga/Outbox territory, Phase 4/5). Safe here because only the exclusive
/// holder can reach this point per seat.
/// </summary>
internal static class ConfirmHoldScript
{
    /// <remarks>
    /// KEYS[1]  = holdseats:{holdId} reverse-index set
    /// KEYS[2]  = sold set key
    /// ARGV[1]  = eventId
    /// ARGV[2]  = holdId
    ///
    /// Same "build the per-seat key inside the script" simplification as ReleaseHoldScript —
    /// see its remarks for why that's fine on a single Redis node.
    ///
    /// Returns the seat ids that were actually confirmed sold.
    /// </remarks>
    public const string LegacySource = """
        local hold_seats_key = KEYS[1]
        local sold_key = KEYS[2]
        local event_id = ARGV[1]
        local hold_id = ARGV[2]

        local seat_ids = redis.call('SMEMBERS', hold_seats_key)
        local confirmed = {}

        for _, seat_id in ipairs(seat_ids) do
            local hold_key = 'hold:' .. event_id .. ':' .. seat_id
            if redis.call('GET', hold_key) == hold_id then
                redis.call('SADD', sold_key, seat_id)
                redis.call('DEL', hold_key)
                table.insert(confirmed, seat_id)
            end
        end

        redis.call('DEL', hold_seats_key)

        return confirmed
        """;

    /// <summary>
    /// Typed compare-and-set confirmation. Validation of the complete tuple and exact seat set
    /// happens before any write. The order claim, confirmed marker and canonical sorted set are
    /// written before the held reverse index is removed, all inside one Lua execution, so
    /// response-loss replay returns the original outcome instead of reporting a missing hold.
    /// </summary>
    public const string Source = """
        local hold_seats_key = KEYS[1]
        local sold_key = KEYS[2]
        local confirmed_seats_key = KEYS[3]
        local confirmed_meta_key = KEYS[4]
        local confirmed_order_key = KEYS[5]
        local hold_id = ARGV[1]
        local order_id = ARGV[2]
        local owner_id = ARGV[3]
        local generation = ARGV[4]
        local seat_count = (#KEYS - 5) / 2
        local expected_hold = 'H|' .. hold_id .. '|' .. owner_id .. '|' .. generation
        local expected_confirmed = 'S|' .. hold_id .. '|' .. order_id .. '|' .. owner_id .. '|' .. generation
        local expected_order = 'O|' .. hold_id .. '|' .. owner_id .. '|' .. generation

        local requested = {}
        for i = 1, seat_count do requested[i] = ARGV[i + 4] end
        table.sort(requested)
        local canonical_seats = table.concat(requested, ',')
        local expected_meta = expected_confirmed .. '|' .. canonical_seats

        local existing_order = redis.call('GET', confirmed_order_key)
        if existing_order and existing_order ~= expected_order then
            return {'CONFLICT', 'order-already-confirmed-by-another-hold'}
        end

        local existing_meta = redis.call('GET', confirmed_meta_key)
        if existing_meta then
            if existing_meta ~= expected_meta
                or redis.call('SCARD', confirmed_seats_key) ~= seat_count then
                return {'CONFLICT', 'confirmed-marker-mismatch'}
            end

            for _, seat_id in ipairs(requested) do
                if redis.call('SISMEMBER', confirmed_seats_key, seat_id) == 0 then
                    return {'CONFLICT', 'confirmed-seat-set-mismatch'}
                end
            end

            redis.call('SET', confirmed_order_key, expected_order)
            local replay = {'ALREADY_APPLIED_SAME'}
            for _, seat_id in ipairs(requested) do table.insert(replay, seat_id) end
            return replay
        elseif redis.call('SCARD', confirmed_seats_key) > 0 then
            return {'CONFLICT', 'confirmed-marker-missing'}
        end

        if redis.call('SCARD', hold_seats_key) == 0 then
            return {'MISSING_OR_EXPIRED'}
        end

        if redis.call('SCARD', hold_seats_key) ~= seat_count then
            return {'CONFLICT', 'held-seat-count-mismatch'}
        end

        for _, seat_id in ipairs(requested) do
            if redis.call('SISMEMBER', hold_seats_key, seat_id) == 0 then
                return {'CONFLICT', 'held-seat-set-mismatch'}
            end
        end

        local missing = false
        for i = 1, seat_count do
            local hold_key = KEYS[i + 5]
            local current = redis.call('GET', hold_key)
            if not current then
                missing = true
            elseif current ~= expected_hold then
                return {'CONFLICT', 'held-owner-or-generation-mismatch'}
            end
        end

        if missing then return {'MISSING_OR_EXPIRED'} end

        for i = 1, seat_count do
            local seat_id = ARGV[i + 4]
            local hold_key = KEYS[i + 5]
            local allocation_key = KEYS[i + 5 + seat_count]
            redis.call('SADD', sold_key, seat_id)
            redis.call('SET', allocation_key, expected_confirmed)
            redis.call('SADD', confirmed_seats_key, seat_id)
            redis.call('DEL', hold_key)
        end
        redis.call('SET', confirmed_order_key, expected_order)
        redis.call('SET', confirmed_meta_key, expected_meta)
        redis.call('DEL', hold_seats_key)

        local applied = {'APPLIED'}
        for _, seat_id in ipairs(requested) do table.insert(applied, seat_id) end
        return applied
        """;
}
