namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>
/// Removes a Released/Expired PostgreSQL allocation from Redis only when every present token is
/// the exact previous owner/generation. A newer owner makes this a conflict rather than a delete.
/// </summary>
internal static class ExpireCachedAllocationScript
{
    /// <remarks>
    /// KEYS: hold, allocation, sold, quarantine, held reverse, confirmed set, confirmed meta.
    /// ARGV: seat, expected held token, expected confirmed token, legacy bare hold id.
    /// </remarks>
    public const string Source = """
        local current_hold = redis.call('GET', KEYS[1])
        local current_allocation = redis.call('GET', KEYS[2])
        if current_hold and current_hold ~= ARGV[2] and current_hold ~= ARGV[4] then
            return {'CONFLICT'}
        end
        if current_allocation and current_allocation ~= ARGV[3] then return {'CONFLICT'} end

        if current_hold then
            redis.call('DEL', KEYS[1])
            redis.call('SREM', KEYS[5], ARGV[1])
            if redis.call('SCARD', KEYS[5]) == 0 then redis.call('DEL', KEYS[5]) end
        end
        if current_allocation then
            redis.call('DEL', KEYS[2])
            redis.call('SREM', KEYS[6], ARGV[1])
            if redis.call('SCARD', KEYS[6]) == 0 then
                redis.call('DEL', KEYS[6])
                redis.call('DEL', KEYS[7])
            end
            redis.call('SREM', KEYS[3], ARGV[1])
            redis.call('SREM', KEYS[4], ARGV[1])
        end
        return {'APPLIED'}
        """;
}
