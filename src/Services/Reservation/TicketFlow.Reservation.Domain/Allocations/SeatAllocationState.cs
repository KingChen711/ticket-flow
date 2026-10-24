namespace TicketFlow.Reservation.Domain.Allocations;

public enum SeatAllocationState
{
    Held,
    Confirmed,
    Released,
    Expired,
}

/// <summary>
/// Result of an owner-scoped compensation attempt on one seat.
/// <see cref="OwnershipChanged"/> is a deliberate, metric-worthy no-op — not a failure.
/// </summary>
public enum SeatAllocationReleaseOutcome
{
    Released,
    AlreadyReleased,
    OwnershipChanged,
}
