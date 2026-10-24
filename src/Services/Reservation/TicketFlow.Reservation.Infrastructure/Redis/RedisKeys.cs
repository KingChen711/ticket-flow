namespace TicketFlow.Reservation.Infrastructure.Redis;

/// <summary>Central place for the Redis key scheme so every method agrees on it.</summary>
internal static class RedisKeys
{
    /// <summary>Set of every valid seat id for the event — the "is this seat real" check.</summary>
    public static string SeatsSet(Guid eventId) => $"seats:{eventId}";

    /// <summary>Set of seat ids already sold for the event.</summary>
    public static string SoldSet(Guid eventId) => $"sold:{eventId}";

    /// <summary>Per-seat hold key. Value = holdId, TTL = hold duration. Presence == Held.</summary>
    public static string Hold(Guid eventId, Guid seatId) => $"hold:{eventId}:{seatId}";

    /// <summary>
    /// Reverse index: which seat ids does this hold cover? Without this, Release/Confirm
    /// (which only receive holdId, not the seat ids) would have no way to find the right
    /// "hold:{eventId}:{seatId}" keys short of scanning every seat in the event. Written
    /// atomically alongside the hold keys in the same Lua script (Commit 5), with the same TTL.
    /// </summary>
    public static string HoldSeatsSet(Guid holdId) => $"holdseats:{holdId}";

    /// <summary>Durable-cache metadata for a confirmed allocation. Value includes the complete
    /// hold/order/owner/generation token so a lost response can be replayed without guessing from
    /// the coarse sold set.</summary>
    public static string Allocation(Guid eventId, Guid seatId) => $"allocation:{eventId}:{seatId}";

    /// <summary>The exact seat set confirmed by one hold. Kept after confirmation for replay.</summary>
    public static string ConfirmedSeatsSet(Guid eventId, Guid holdId) => $"confirmedseats:{eventId}:{holdId}";

    /// <summary>Confirmation marker: hold/order/owner/generation plus canonical sorted seat set.</summary>
    public static string ConfirmedMeta(Guid eventId, Guid holdId) => $"confirmedmeta:{eventId}:{holdId}";

    /// <summary>Atomic cache-side claim preventing two different holds from confirming the same
    /// order before PostgreSQL can commit the unique quote-to-order mapping.</summary>
    public static string ConfirmedOrder(Guid orderId) => $"confirmedorder:{orderId}";

    /// <summary>Monotonic PostgreSQL snapshot version last applied to this event cache.</summary>
    public static string RebuildVersion(Guid eventId) => $"rebuildversion:{eventId}";

    /// <summary>Short fail-closed lock around a cache snapshot compare-and-swap.</summary>
    public static string RebuildLock(Guid eventId) => $"rebuildlock:{eventId}";

    /// <summary>Seats deliberately made unavailable because ownership is ambiguous.</summary>
    public static string QuarantinedSet(Guid eventId) => $"quarantined:{eventId}";
}
