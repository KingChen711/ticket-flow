using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Abstractions;

/// <summary>
/// Redis side — the live availability + holds. This is where the anti-oversell concurrency
/// control lives (atomic Lua, see the Infrastructure implementation in Nhóm B). Ports only in
/// this commit; the Redis-backed implementation lands in Commit 4–6.
/// </summary>
/// <remarks>
/// The authority-aware (owner + generation) members below carry no default body on purpose. They
/// were introduced in Commit 4/5 as default interface methods that fell back to the pre-Phase-12
/// primitives, so that rolling deployments and existing test doubles kept compiling. The Nhóm B
/// audit found what that cost: because <c>ReleaseAllocationAsync</c>'s adapter forwarded to an
/// unconditional sold-set removal, EVERY test double silently reversed a confirmed allocation with
/// no ownership check at all — including the decorator that wraps a real Redis store — so
/// ReleaseAllocationScript, the compare-and-set Commit 5 exists to provide, was executed by no
/// test in the repository. A missing override has to be a compile error; the fallback made it
/// invisible instead. Production has overridden all of these since Commit 5, so the compatibility
/// window they were written for is closed (mục 10's contract step).
/// </remarks>
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
    /// Release an exact active quote only when its owner and generation still match the cached
    /// ownership token. A replay after the cache entry expired is successful and must not touch a
    /// newer holder.
    /// </summary>
    Task<ReleaseHoldStoreResult> ReleaseAsync(
        ReleaseHoldStoreRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Move the seats held under <paramref name="holdId"/> into the sold set (Redis side) and
    /// return those seat ids so the caller can persist them to Postgres. Empty list means the
    /// hold is no longer valid (expired / released / already confirmed).
    /// </summary>
    Task<IReadOnlyList<Guid>> ConfirmAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default);

    /// <summary>Effective live status per seat (Available / Held / Sold) computed from Redis.</summary>
    Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default);

    /// <summary>V2 authority-aware hold: owner and generation participate in the Lua compare-and-set.</summary>
    Task<HoldStoreResult> TryHoldAsync(
        HoldStoreRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V2 exact-set confirmation. An implementation must distinguish an idempotent replay of the
    /// same owner/generation/order from an expired hold or a conflicting allocation.
    /// </summary>
    Task<ConfirmHoldStoreResult> ConfirmAsync(
        ConfirmHoldStoreRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V2 owner-scoped reversal of a confirmed allocation: a compare-and-set that leaves a seat
    /// already resold to a newer owner untouched and reports it as stale.
    /// </summary>
    Task<ReleaseAllocationStoreResult> ReleaseAllocationAsync(
        ReleaseAllocationStoreRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Restore one authoritative allocation into the cache after a recoverable drift.</summary>
    Task RestoreAsync(
        RestoreSeatAllocationRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This seat-hold store does not support allocation restore yet.");

    /// <summary>Atomically publish a versioned event cache snapshot during bootstrap/rebuild.</summary>
    Task<RebuildSeatCacheResult> RebuildAsync(
        RebuildSeatCacheRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This seat-hold store does not support cache rebuild yet.");
}
