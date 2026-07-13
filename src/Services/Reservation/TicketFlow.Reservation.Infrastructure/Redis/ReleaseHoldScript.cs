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
    /// Returns the count of seats actually released.
    /// </remarks>
    public const string Source = """
        local hold_seats_key = KEYS[1]
        local event_id = ARGV[1]
        local hold_id = ARGV[2]

        local seat_ids = redis.call('SMEMBERS', hold_seats_key)
        local released = 0

        for _, seat_id in ipairs(seat_ids) do
            local hold_key = 'hold:' .. event_id .. ':' .. seat_id
            if redis.call('GET', hold_key) == hold_id then
                redis.call('DEL', hold_key)
                released = released + 1
            end
        end

        redis.call('DEL', hold_seats_key)

        return released
        """;
}
