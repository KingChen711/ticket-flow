namespace TicketFlow.Reservation.Application.Abstractions;

public sealed record HoldStoreRequest(
    Guid EventId,
    IReadOnlyList<Guid> SeatIds,
    Guid HoldId,
    Guid OwnerUserId,
    long Generation,
    TimeSpan Ttl);

public enum HoldStoreOutcome
{
    Held,
    Unavailable,
    Conflict,
}

public sealed record HoldStoreResult(
    HoldStoreOutcome Outcome,
    IReadOnlyList<Guid> UnavailableSeatIds)
{
    public static HoldStoreResult Succeeded() => new(HoldStoreOutcome.Held, []);

    public static HoldStoreResult Unavailable(IReadOnlyList<Guid> seatIds) =>
        new(HoldStoreOutcome.Unavailable, seatIds);
}

public sealed record ConfirmHoldStoreRequest(
    Guid EventId,
    Guid HoldId,
    Guid OrderId,
    Guid OwnerUserId,
    long Generation,
    IReadOnlyList<Guid> SeatIds);

public enum ConfirmHoldStoreOutcome
{
    Applied,
    AlreadyAppliedSame,
    MissingOrExpired,
    Conflict,
}

public sealed record ConfirmHoldStoreResult(
    ConfirmHoldStoreOutcome Outcome,
    IReadOnlyList<Guid> SeatIds,
    string? Reason = null)
{
    public bool IsSuccessful =>
        Outcome is ConfirmHoldStoreOutcome.Applied or ConfirmHoldStoreOutcome.AlreadyAppliedSame;
}

public sealed record ReleaseHoldStoreRequest(
    Guid EventId,
    Guid HoldId,
    Guid OwnerUserId,
    long Generation,
    IReadOnlyList<Guid> SeatIds);

public enum ReleaseHoldStoreOutcome
{
    Applied,
    AlreadyReleased,
    Conflict,
}

public sealed record ReleaseHoldStoreResult(
    ReleaseHoldStoreOutcome Outcome,
    string? Reason = null)
{
    public bool IsSuccessful =>
        Outcome is ReleaseHoldStoreOutcome.Applied or ReleaseHoldStoreOutcome.AlreadyReleased;
}

/// <summary>
/// Owner-scoped reversal of a CONFIRMED cache allocation. Unlike
/// <see cref="ReleaseHoldStoreRequest"/> (which reverses an active hold), this undoes the sold
/// marker written by a confirmation — and only when the cached token still names this exact
/// hold/order/owner/generation.
/// </summary>
public sealed record ReleaseAllocationStoreRequest(
    Guid EventId,
    Guid HoldId,
    Guid OrderId,
    Guid OwnerUserId,
    long Generation,
    IReadOnlyList<Guid> SeatIds);

public enum ReleaseAllocationStoreOutcome
{
    Applied,
    AlreadyReleased,
    OwnershipChanged,
    Conflict,
}

public sealed record ReleaseAllocationStoreResult(
    ReleaseAllocationStoreOutcome Outcome,
    IReadOnlyList<Guid> ReleasedSeatIds,
    IReadOnlyList<Guid> StaleSeatIds,
    string? Reason = null)
{
    public bool IsSuccessful =>
        Outcome is ReleaseAllocationStoreOutcome.Applied
            or ReleaseAllocationStoreOutcome.AlreadyReleased
            or ReleaseAllocationStoreOutcome.OwnershipChanged;
}

public enum CachedAllocationState
{
    Held,
    Confirmed,
    Quarantined,
}

public sealed record CachedSeatAllocation(
    Guid SeatId,
    Guid HoldId,
    Guid OwnerUserId,
    long Generation,
    CachedAllocationState State,
    Guid? OrderId,
    DateTimeOffset? ExpiresAt);

public sealed record RestoreSeatAllocationRequest(
    Guid EventId,
    CachedSeatAllocation Allocation);

public sealed record RebuildSeatCacheRequest(
    Guid EventId,
    IReadOnlyList<Guid> ValidSeatIds,
    IReadOnlyList<CachedSeatAllocation> Allocations,
    long SnapshotVersion,
    IReadOnlyList<Guid>? QuarantinedSeatIds = null);

public sealed record RebuildSeatCacheResult(
    bool Applied,
    IReadOnlyList<Guid> ConflictingSeatIds,
    long SnapshotVersion);
