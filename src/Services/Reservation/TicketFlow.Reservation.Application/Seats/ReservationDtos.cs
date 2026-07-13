namespace TicketFlow.Reservation.Application.Seats;

public sealed record ProvisionSectionRequest(Guid SectionId, string SectionName, int Capacity);

public sealed record ProvisionInventoryRequest(Guid EventId, IReadOnlyList<ProvisionSectionRequest> Sections);

public sealed record SeatDto(Guid Id, Guid SectionId, string Label, string Status);

public sealed record EventAvailabilityResponse(Guid EventId, IReadOnlyList<SeatDto> Seats);

// Hold ownership in Phase 2 is by possession of the returned HoldId (a bearer capability),
// not by a user id — associating holds to authenticated users is Phase 5/7. So the request
// carries no HolderId (would be dead code now — see docs/phase-2-plan.md).
public sealed record HoldSeatsRequest(Guid EventId, IReadOnlyList<Guid> SeatIds);

public sealed record HoldSeatsResult(
    bool Success,
    Guid? HoldId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<Guid> UnavailableSeatIds)
{
    public static HoldSeatsResult Succeeded(Guid holdId, DateTimeOffset expiresAt) => new(true, holdId, expiresAt, []);
    public static HoldSeatsResult Unavailable(IReadOnlyList<Guid> seatIds) => new(false, null, null, seatIds);
}
