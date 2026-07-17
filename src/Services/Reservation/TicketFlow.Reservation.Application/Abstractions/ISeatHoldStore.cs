using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Abstractions;

/// <summary>
/// Redis side — the live availability + holds. This is where the anti-oversell concurrency
/// control lives (atomic Lua, see the Infrastructure implementation in Nhóm B). Ports only in
/// this commit; the Redis-backed implementation lands in Commit 4–6.
/// </summary>
public interface ISeatHoldStore
{
    /// <summary>Register the valid seat ids for an event so holds can validate against them.</summary>
    Task LoadInventoryAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomic all-or-nothing hold. Returns the seat ids that were NOT available — an empty list
    /// means every requested seat was held under <paramref name="holdId"/>. Exactly one
    /// concurrent caller can win any given seat.
    /// </summary>
    Task<IReadOnlyList<Guid>> TryHoldAsync(Guid eventId, IReadOnlyList<Guid> seatIds, Guid holdId, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Release every seat currently held under <paramref name="holdId"/>. Returns the
    /// seat ids actually released — empty means the hold was no longer valid.</summary>
    Task<IReadOnlyList<Guid>> ReleaseAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Move the seats held under <paramref name="holdId"/> into the sold set (Redis side) and
    /// return those seat ids so the caller can persist them to Postgres. Empty list means the
    /// hold is no longer valid (expired / released / already confirmed).
    /// </summary>
    Task<IReadOnlyList<Guid>> ConfirmAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default);

    /// <summary>Effective live status per seat (Available / Held / Sold) computed from Redis.</summary>
    Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes seat ids from the durable Sold set (Redis side) — compensates a CONFIRMED sale
    /// (Commit 9), unlike ReleaseAsync which only releases an active, not-yet-confirmed hold (see
    /// Seat.MarkAvailable's doc comment for why these are genuinely different operations).
    /// </summary>
    Task RemoveFromSoldAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default);
}
