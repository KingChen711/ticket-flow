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
}
