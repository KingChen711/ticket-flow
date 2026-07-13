namespace TicketFlow.Reservation.Domain.Seats;

public enum SeatStatus
{
    /// <summary>Neither sold nor currently held.</summary>
    Available,

    /// <summary>
    /// Temporarily reserved with a TTL. This value is NEVER persisted to Postgres — a hold
    /// lives only in Redis and expires on its own. It is computed at query time by combining
    /// the durable seat (Available/Sold) with the presence of a Redis hold key.
    /// </summary>
    Held,

    /// <summary>Durably purchased. Persisted to Postgres.</summary>
    Sold
}
