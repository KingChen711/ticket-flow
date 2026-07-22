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

public sealed record HoldSeatsResult(
    bool Success,
    Guid? HoldId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<Guid> UnavailableSeatIds,
    HoldQuoteResponse? Quote)
{
    public static HoldSeatsResult Succeeded(HoldQuoteResponse quote) =>
        new(true, quote.HoldId, quote.ExpiresAt, [], quote);

    public static HoldSeatsResult Unavailable(IReadOnlyList<Guid> seatIds) =>
        new(false, null, null, seatIds, null);
}

/// <summary>
/// Answers "can these exact seats be sold right now" for a caller that doesn't already know the
/// seat ids are legitimate (e.g. ordering, over gRPC) — unlike EventAvailabilityResponse, this
/// must also treat an unknown/unprovisioned seat id as unavailable rather than silently reporting
/// it Available (see docs/phase-2-plan.md, Commit 8).
/// </summary>
public sealed record CheckAvailabilityResult(bool AllAvailable, IReadOnlyList<Guid> UnavailableSeatIds);
