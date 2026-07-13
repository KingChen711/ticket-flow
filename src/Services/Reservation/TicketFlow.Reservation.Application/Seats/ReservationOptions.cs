namespace TicketFlow.Reservation.Application.Seats;

public sealed class ReservationOptions
{
    /// <summary>How long a hold lives before Redis expires it automatically. Scope mục 7: 10 phút.</summary>
    public int HoldTtlMinutes { get; set; } = 10;
}
