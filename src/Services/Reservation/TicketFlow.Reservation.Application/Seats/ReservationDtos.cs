namespace TicketFlow.Reservation.Application.Seats;

public sealed record ProvisionSectionRequest(Guid SectionId, string SectionName, int Capacity);

public sealed record ProvisionInventoryRequest(Guid EventId, IReadOnlyList<ProvisionSectionRequest> Sections);

public sealed record SeatDto(Guid Id, Guid SectionId, string Label, string Status);

public sealed record EventAvailabilityResponse(Guid EventId, IReadOnlyList<SeatDto> Seats);

public sealed record HoldSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds, Guid OwnerUserId);

public sealed record HoldQuoteResponse(
    Guid HoldId,
    Guid EventId,
    Guid OwnerUserId,
    IReadOnlyList<Guid> SeatIds,
    decimal Amount,
    string Currency,
    long PriceSnapshotVersion,
    long Generation,
    DateTimeOffset ExpiresAt);

public sealed record ConfirmSeatAllocationRequest(
    Guid OrderId,
    Guid EventId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds,
    Guid OwnerUserId,
    long Generation,
    long PriceSnapshotVersion,
    decimal Amount,
    string Currency,
    string? OperationKey = null);

public sealed record ConfirmSeatAllocationResult(
    bool Succeeded,
    Guid OrderId,
    Guid EventId,
    Guid HoldId,
    Guid OwnerUserId,
    long Generation,
    IReadOnlyList<Guid> SeatIds,
    Guid AllocationReference,
    long PriceSnapshotVersion,
    decimal Amount,
    string Currency,
    DateTimeOffset DecidedAt,
    string? FailureCode = null,
    string? FailureReason = null);

/// <summary>
/// Why a hold was refused. The caller must be able to tell "these seats are taken" apart from
/// "you are holding too much / too often" — otherwise a per-user cap looks identical to a real
/// sold-out event, both to the UI and to anyone probing the API (plan Commit 5 delta).
/// </summary>
public enum HoldRejectionReason
{
    None,
    SeatsUnavailable,
    HoldCapExceeded,
    RateLimited,
}

public sealed record HoldSeatsResult(
    bool Success,
    Guid? HoldId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<Guid> UnavailableSeatIds,
    HoldQuoteResponse? Quote,
    HoldRejectionReason Reason = HoldRejectionReason.None,
    string? ReasonDetail = null)
{
    public static HoldSeatsResult Succeeded(HoldQuoteResponse quote) =>
        new(true, quote.HoldId, quote.ExpiresAt, [], quote);

    public static HoldSeatsResult Unavailable(IReadOnlyList<Guid> seatIds) =>
        new(false, null, null, seatIds, null, HoldRejectionReason.SeatsUnavailable);

    public static HoldSeatsResult CapExceeded(string detail) =>
        new(false, null, null, [], null, HoldRejectionReason.HoldCapExceeded, detail);

    public static HoldSeatsResult RateLimited(string detail) =>
        new(false, null, null, [], null, HoldRejectionReason.RateLimited, detail);
}

/// <summary>
/// Owner-scoped compensation request. Every authority field is an <em>expectation</em>: the
/// service releases only what still matches it, and reports the rest as stale.
/// </summary>
public sealed record ReleaseSeatAllocationRequest(
    Guid OrderId,
    Guid EventId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds,
    Guid? OwnerUserId,
    long Generation,
    string? OperationKey = null);

public sealed record ReleaseSeatAllocationResult(
    bool Succeeded,
    Guid OrderId,
    Guid EventId,
    Guid HoldId,
    Guid? OwnerUserId,
    long Generation,
    IReadOnlyList<Guid> ReleasedSeatIds,
    IReadOnlyList<Guid> StaleSeatIds,
    Guid OperationReference,
    DateTimeOffset DecidedAt,
    string? FailureCode = null,
    string? FailureReason = null);

/// <summary>
/// Answers "can these exact seats be sold right now" for a caller that doesn't already know the
/// seat ids are legitimate (e.g. ordering, over gRPC) — unlike EventAvailabilityResponse, this
/// must also treat an unknown/unprovisioned seat id as unavailable rather than silently reporting
/// it Available (see docs/phase-2-plan.md, Commit 8).
/// </summary>
public sealed record CheckAvailabilityResult(bool AllAvailable, IReadOnlyList<Guid> UnavailableSeatIds);
