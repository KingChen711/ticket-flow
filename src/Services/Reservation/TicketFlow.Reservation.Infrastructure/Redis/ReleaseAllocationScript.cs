namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Owner-scoped reversal of a CONFIRMED allocation (Phase 12 Commit 5). The pre-Phase-12 path
/// was a blunt <c>SREM sold:{eventId} seat…</c>: it carried no ownership token at all, so a
/// compensation for order A arriving after the seat had been resold to order B silently made B's
/// paid seat available again. Here every seat is a compare-and-set against the exact
/// <c>S|hold|order|owner|generation</c> token that the confirmation wrote, and a seat whose token
/// has moved on is reported as stale rather than touched.
/// </summary>
internal static class ReleaseAllocationScript
{
    /// <remarks>
    /// KEYS[1]              = sold set key
    /// KEYS[2]              = confirmedseats:{eventId}:{holdId} set
    /// KEYS[3]              = confirmedmeta:{eventId}:{holdId}
    /// KEYS[4]              = confirmedorder:{orderId}
    /// KEYS[5..N+4]         = allocation:{eventId}:{seatId}, one per seat, sorted
    /// ARGV[1]              = holdId
    /// ARGV[2]              = orderId
    /// ARGV[3]              = ownerUserId
    /// ARGV[4]              = generation
    /// ARGV[5..N+4]         = seat ids, parallel to KEYS[5..N+4]
    ///
    /// Returns {status, released_count, released…, stale…} where status is APPLIED,
    /// ALREADY_RELEASED (nothing matched and nothing conflicted) or OWNERSHIP_CHANGED (at least
    /// one seat now belongs to someone else). The hold-level markers are only cleaned up when no
    /// seat was left stale, so a conflict keeps its evidence for the reconciler.
    /// </remarks>
    public const string Source = """
        local sold_key = KEYS[1]
        local confirmed_seats_key = KEYS[2]
        local confirmed_meta_key = KEYS[3]
        local confirmed_order_key = KEYS[4]
        local hold_id = ARGV[1]
        local order_id = ARGV[2]
        local owner_id = ARGV[3]
        local generation = ARGV[4]
        local seat_count = #KEYS - 4
        local expected_allocation = 'S|' .. hold_id .. '|' .. order_id .. '|' .. owner_id .. '|' .. generation
        local expected_order = 'O|' .. hold_id .. '|' .. owner_id .. '|' .. generation

        local released = {}
        local stale = {}

        for i = 1, seat_count do
            local allocation_key = KEYS[i + 4]
            local seat_id = ARGV[i + 4]
            local current = redis.call('GET', allocation_key)

            if current == expected_allocation then
                redis.call('DEL', allocation_key)
                redis.call('SREM', sold_key, seat_id)
                redis.call('SREM', confirmed_seats_key, seat_id)
                table.insert(released, seat_id)
            elseif current then
                -- A different hold/order/owner/generation owns this seat now.
                table.insert(stale, seat_id)
            elseif redis.call('SISMEMBER', sold_key, seat_id) == 1 then
                -- Sold without an ownership token: ambiguous, never silently freed.
                table.insert(stale, seat_id)
            end
        end

        if #stale == 0 then
            redis.call('DEL', confirmed_seats_key)
            redis.call('DEL', confirmed_meta_key)
            if redis.call('GET', confirmed_order_key) == expected_order then
                redis.call('DEL', confirmed_order_key)
            end
        end

        local status
        if #stale > 0 then
            status = 'OWNERSHIP_CHANGED'
        elseif #released > 0 then
            status = 'APPLIED'
        else
            status = 'ALREADY_RELEASED'
        end

        local result = {status, tostring(#released)}
        for _, seat_id in ipairs(released) do table.insert(result, seat_id) end
        for _, seat_id in ipairs(stale) do table.insert(result, seat_id) end
        return result
        """;
}
