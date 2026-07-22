namespace TicketFlow.Reservation.Domain.Seats;

public enum SeatStatus
{
    /// <summary>Neither sold nor currently held.</summary>
    Available,

    /// <summary>
    /// Temporarily reserved with a TTL. This value is never persisted on the Seat row; an active
    /// HoldQuote is the durable authority and Redis is its expiring acceleration view.
    /// </summary>
    Held,

    /// <summary>Durably purchased. Persisted to Postgres.</summary>
    Sold
}
