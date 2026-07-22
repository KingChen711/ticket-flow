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
    /// Release an exact active quote only when its owner and generation still match the cached
    /// ownership token. A replay after the cache entry expired is successful and must not touch a
    /// newer holder.
    /// </summary>
    async Task<ReleaseHoldStoreResult> ReleaseAsync(
        ReleaseHoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var released = await ReleaseAsync(request.EventId, request.HoldId, cancellationToken);
        return released.Count == 0
            ? new ReleaseHoldStoreResult(ReleaseHoldStoreOutcome.AlreadyReleased)
            : new ReleaseHoldStoreResult(ReleaseHoldStoreOutcome.Applied);
    }

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

    /// <summary>
    /// V2 authority-aware hold. The default adapter keeps rolling deployments and existing test
    /// doubles source-compatible; the Redis implementation must override it before v1 keys are
    /// retired so owner and generation participate in the Lua compare-and-set.
    /// </summary>
    async Task<HoldStoreResult> TryHoldAsync(
        HoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var unavailable = await TryHoldAsync(
            request.EventId,
            request.SeatIds,
            request.HoldId,
            request.Ttl,
            cancellationToken);

        return unavailable.Count == 0
            ? HoldStoreResult.Succeeded()
            : HoldStoreResult.Unavailable(unavailable);
    }

    /// <summary>
    /// V2 exact-set confirmation. An implementation must distinguish an idempotent replay of the
    /// same owner/generation/order from an expired hold or a conflicting allocation.
    /// </summary>
    async Task<ConfirmHoldStoreResult> ConfirmAsync(
        ConfirmHoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var seatIds = await ConfirmAsync(request.EventId, request.HoldId, cancellationToken);
        return seatIds.Count == 0
            ? new ConfirmHoldStoreResult(ConfirmHoldStoreOutcome.MissingOrExpired, [])
            : new ConfirmHoldStoreResult(ConfirmHoldStoreOutcome.Applied, seatIds);
    }

    /// <summary>
    /// V2 owner-scoped reversal of a confirmed allocation. The default adapter falls back to the
    /// blunt v1 sold-set removal so existing test doubles keep compiling; the Redis implementation
    /// overrides it with a compare-and-set that leaves a newer owner's seat untouched.
    /// </summary>
    async Task<ReleaseAllocationStoreResult> ReleaseAllocationAsync(
        ReleaseAllocationStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await RemoveFromSoldAsync(request.EventId, request.SeatIds, cancellationToken);
        return new ReleaseAllocationStoreResult(
            ReleaseAllocationStoreOutcome.Applied,
            request.SeatIds,
            []);
    }

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
