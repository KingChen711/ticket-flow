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
    public const string Source = """
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
}
