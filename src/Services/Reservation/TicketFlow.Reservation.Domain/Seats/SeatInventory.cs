using TicketFlow.Domain;

namespace TicketFlow.Reservation.Domain.Seats;

/// <summary>
/// Explodes a section's plain capacity count (all Catalog modeled in Phase 1) into individual,
/// addressable Seat entities labeled "{sectionName}-{n}". This is the concrete resolution of
/// the Phase-1 deferral: seats must be identifiable so 1000 buyers can contend for one exact
/// seat (the Phase-2 DoD). No row/column grid yet — that visual layout is Phase 8's concern.
/// </summary>
public static class SeatInventory
{
    public static IReadOnlyList<Seat> ForSection(Guid eventId, Guid sectionId, string sectionName, int capacity)
    {
        Guard.NotNullOrEmpty(sectionName, nameof(sectionName));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");

        var seats = new List<Seat>(capacity);
        for (var n = 1; n <= capacity; n++)
            seats.Add(Seat.Create(eventId, sectionId, $"{sectionName}-{n}"));

        return seats;
    }
}
