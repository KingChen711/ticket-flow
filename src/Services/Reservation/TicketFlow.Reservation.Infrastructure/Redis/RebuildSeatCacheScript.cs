namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Event-scoped cache snapshot compare-and-swap. Redis executes the conflict preflight and the
/// replacement atomically; a different live owner aborts before any key is changed. The caller
/// also owns a short lock, checked here, which makes concurrent bootstrap attempts fail closed.
/// </summary>
internal static class RebuildSeatCacheScript
{
    /// <remarks>
    /// KEYS: valid seats, sold, quarantine, version, rebuild lock.
    /// ARGV: lock token, event id, snapshot version, now unix-ms, valid count + ids,
    ///       allocation count + tuples(seat,state,hold,order,owner,generation,expires unix-ms),
    ///       explicit quarantine count + seat ids.
    /// </remarks>
    public const string Source = """
        local valid_key = KEYS[1]
        local sold_key = KEYS[2]
        local quarantine_key = KEYS[3]
        local version_key = KEYS[4]
        local lock_key = KEYS[5]
        local lock_token = ARGV[1]
        local event_id = ARGV[2]
        local snapshot_version = tonumber(ARGV[3])
        local now_ms = tonumber(ARGV[4])

        if redis.call('GET', lock_key) ~= lock_token then
            return {'LOCK_LOST'}
        end

        local current_version = tonumber(redis.call('GET', version_key) or '-1')
        if current_version > snapshot_version then
            return {'STALE'}
        end

        local cursor = 5
        local valid_count = tonumber(ARGV[cursor])
        cursor = cursor + 1
        local valid = {}
        local valid_lookup = {}
        for i = 1, valid_count do
            local seat_id = ARGV[cursor]
            cursor = cursor + 1
            valid[i] = seat_id
            valid_lookup[seat_id] = true
        end

        local allocation_count = tonumber(ARGV[cursor])
        cursor = cursor + 1
        local allocations = {}
        local desired = {}
        local desired_holds = {}
        for i = 1, allocation_count do
            local allocation = {
                seat = ARGV[cursor], state = ARGV[cursor + 1], hold = ARGV[cursor + 2],
                order = ARGV[cursor + 3], owner = ARGV[cursor + 4],
                generation = ARGV[cursor + 5], expires = tonumber(ARGV[cursor + 6])
            }
            cursor = cursor + 7
            allocations[i] = allocation
            desired[allocation.seat] = allocation
            desired_holds[allocation.hold] = true
        end

        local quarantine_count = tonumber(ARGV[cursor])
        cursor = cursor + 1
        local explicit_quarantine = {}
        local quarantine_lookup = {}
        for i = 1, quarantine_count do
            local seat_id = ARGV[cursor]
            cursor = cursor + 1
            explicit_quarantine[i] = seat_id
            quarantine_lookup[seat_id] = true
        end

        local conflicts = {}
        for _, seat_id in ipairs(valid) do
            local allocation = desired[seat_id]
            local hold_key = 'hold:' .. event_id .. ':' .. seat_id
            local allocation_key = 'allocation:' .. event_id .. ':' .. seat_id
            local current_hold = redis.call('GET', hold_key)
            local current_allocation = redis.call('GET', allocation_key)
            local quarantined = redis.call('SISMEMBER', quarantine_key, seat_id)

            if quarantine_lookup[seat_id] then
                -- Explicit containment keeps any conflicting owner tokens for diagnosis while
                -- making the seat unavailable. Safe seats in the same snapshot still converge.
            elseif allocation then
                local held_token = 'H|' .. allocation.hold .. '|' .. allocation.owner .. '|' .. allocation.generation
                local confirmed_token = 'S|' .. allocation.hold .. '|' .. allocation.order .. '|' .. allocation.owner .. '|' .. allocation.generation
                if allocation.state == 'H' then
                    if quarantined == 1
                        or (current_hold and current_hold ~= held_token)
                        or current_allocation then
                        table.insert(conflicts, seat_id)
                    end
                elseif allocation.state == 'S' then
                    local order_key = 'confirmedorder:' .. allocation.order
                    local order_token = 'O|' .. allocation.hold .. '|' .. allocation.owner .. '|' .. allocation.generation
                    local current_order = redis.call('GET', order_key)
                    if quarantined == 1
                        or (current_hold and current_hold ~= held_token)
                        or (current_allocation and current_allocation ~= confirmed_token)
                        or (current_order and current_order ~= order_token) then
                        table.insert(conflicts, seat_id)
                    end
                elseif allocation.state == 'Q' then
                    -- Already ambiguous: rebuilding may only keep it unavailable.
                else
                    table.insert(conflicts, seat_id)
                end
            elseif current_hold or current_allocation or quarantined == 1 then
                -- A cache-only owner is not safe to delete automatically.
                table.insert(conflicts, seat_id)
            end
        end

        if #conflicts > 0 then
            local result = {'CONFLICT'}
            table.sort(conflicts)
            for _, seat_id in ipairs(conflicts) do table.insert(result, seat_id) end
            return result
        end

        -- Preflight passed. This entire replacement remains invisible until the Lua script ends.
        redis.call('DEL', valid_key)
        redis.call('DEL', sold_key)
        redis.call('DEL', quarantine_key)

        -- Reverse indexes are derived snapshots too. Recreate every desired hold marker from
        -- scratch so orphan members without a per-seat token cannot survive a successful repair.
        for hold_id, _ in pairs(desired_holds) do
            redis.call('DEL', 'holdseats:' .. hold_id)
            redis.call('DEL', 'confirmedseats:' .. event_id .. ':' .. hold_id)
            redis.call('DEL', 'confirmedmeta:' .. event_id .. ':' .. hold_id)
        end

        for _, seat_id in ipairs(valid) do
            local hold_key = 'hold:' .. event_id .. ':' .. seat_id
            local allocation_key = 'allocation:' .. event_id .. ':' .. seat_id
            local old_hold = redis.call('GET', hold_key)
            local old_allocation = redis.call('GET', allocation_key)
            local desired_allocation = desired[seat_id]

            if not quarantine_lookup[seat_id]
                and (not desired_allocation or desired_allocation.state ~= 'Q')
                and old_hold and string.sub(old_hold, 1, 2) == 'H|' then
                local old_hold_id = string.match(old_hold, '^H|([^|]+)|')
                if old_hold_id then
                    local old_reverse = 'holdseats:' .. old_hold_id
                    redis.call('SREM', old_reverse, seat_id)
                    if redis.call('SCARD', old_reverse) == 0 then redis.call('DEL', old_reverse) end
                end
            end
            if not quarantine_lookup[seat_id]
                and (not desired_allocation or desired_allocation.state ~= 'Q')
                and old_allocation and string.sub(old_allocation, 1, 2) == 'S|' then
                local old_confirmed_hold, old_confirmed_order, old_confirmed_owner, old_confirmed_generation =
                    string.match(old_allocation, '^S|([^|]+)|([^|]+)|([^|]+)|([^|]+)$')
                if old_confirmed_hold then
                    local old_confirmed = 'confirmedseats:' .. event_id .. ':' .. old_confirmed_hold
                    redis.call('SREM', old_confirmed, seat_id)
                    if redis.call('SCARD', old_confirmed) == 0 then
                        redis.call('DEL', old_confirmed)
                        redis.call('DEL', 'confirmedmeta:' .. event_id .. ':' .. old_confirmed_hold)
                    end
                    local old_order_key = 'confirmedorder:' .. old_confirmed_order
                    local old_order_token = 'O|' .. old_confirmed_hold .. '|' .. old_confirmed_owner .. '|' .. old_confirmed_generation
                    if redis.call('GET', old_order_key) == old_order_token then
                        redis.call('DEL', old_order_key)
                    end
                end
            end

            if not quarantine_lookup[seat_id]
                and (not desired_allocation or desired_allocation.state ~= 'Q') then
                redis.call('DEL', hold_key)
                redis.call('DEL', allocation_key)
            end
            redis.call('SADD', valid_key, seat_id)
        end

        local confirmed_by_hold = {}
        for _, allocation in ipairs(allocations) do
            local seat_id = allocation.seat
            local hold_key = 'hold:' .. event_id .. ':' .. seat_id
            local allocation_key = 'allocation:' .. event_id .. ':' .. seat_id
            local held_token = 'H|' .. allocation.hold .. '|' .. allocation.owner .. '|' .. allocation.generation
            local confirmed_token = 'S|' .. allocation.hold .. '|' .. allocation.order .. '|' .. allocation.owner .. '|' .. allocation.generation

            if allocation.state == 'H' and allocation.expires > now_ms then
                local ttl_ms = allocation.expires - now_ms
                if ttl_ms < 1 then ttl_ms = 1 end
                redis.call('SET', hold_key, held_token, 'PX', ttl_ms)
                local reverse = 'holdseats:' .. allocation.hold
                redis.call('SADD', reverse, seat_id)
                redis.call('PEXPIRE', reverse, ttl_ms)
            elseif allocation.state == 'S' then
                redis.call('SADD', sold_key, seat_id)
                redis.call('SET', allocation_key, confirmed_token)
                redis.call('SET', 'confirmedorder:' .. allocation.order,
                    'O|' .. allocation.hold .. '|' .. allocation.owner .. '|' .. allocation.generation)
                local confirmed_key = 'confirmedseats:' .. event_id .. ':' .. allocation.hold
                redis.call('SADD', confirmed_key, seat_id)
                if not confirmed_by_hold[allocation.hold] then
                    confirmed_by_hold[allocation.hold] = {
                        token = confirmed_token, seats = {}, key = confirmed_key
                    }
                end
                table.insert(confirmed_by_hold[allocation.hold].seats, seat_id)
            elseif allocation.state == 'Q' then
                redis.call('SADD', quarantine_key, seat_id)
                redis.call('SADD', sold_key, seat_id)
            end
        end


        for _, seat_id in ipairs(explicit_quarantine) do
            redis.call('SADD', quarantine_key, seat_id)
            redis.call('SADD', sold_key, seat_id)
        end

        for hold_id, confirmation in pairs(confirmed_by_hold) do
            table.sort(confirmation.seats)
            local canonical = table.concat(confirmation.seats, ',')
            redis.call('SET', 'confirmedmeta:' .. event_id .. ':' .. hold_id,
                confirmation.token .. '|' .. canonical)
        end

        redis.call('SET', version_key, snapshot_version)
        return {'APPLIED'}
        """;

    public const string ReleaseLockSource = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;
}
