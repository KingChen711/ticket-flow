namespace TicketFlow.Reservation.Application.Seats;

public sealed class ReservationOptions
{
    /// <summary>How long a hold lives before Redis expires it automatically. Scope mục 7: 10 phút.</summary>
    public int HoldTtlMinutes { get; set; } = 10;

    /// <summary>Hard ceiling on one request. Was an inline literal before Commit 5.</summary>
    public int MaxSeatsPerHold { get; set; } = 20;

    /// <summary>
    /// Per-user caps (Commit 5 delta). Commit 3 locked down <em>what</em> a client may buy and at
    /// <em>what price</em>, but nothing stopped one authenticated user from re-holding an entire
    /// venue every TTL window without ever paying — a cheap griefing/lock-out attack that the
    /// flash-sale load test cannot see because it only measures oversell, not availability denial.
    /// </summary>
    public int MaxActiveHoldsPerUserPerEvent { get; set; } = 3;

    public int MaxHeldSeatsPerUserPerEvent { get; set; } = 20;

    /// <summary>
    /// Sliding-window creation limit per user per event. Counted from durable hold-quote rows, so
    /// it survives a process restart and cannot be reset by reconnecting — unlike an in-memory
    /// limiter.
    /// </summary>
    public int MaxHoldsPerUserPerWindow { get; set; } = 10;

    public int HoldRateLimitWindowSeconds { get; set; } = 60;

    /// <summary>Ceilings for organizer-side provisioning, so one request cannot allocate a
    /// pathological seat count in a single transaction.</summary>
    public int MaxSectionsPerProvision { get; set; } = 50;

    public int MaxSeatsPerSection { get; set; } = 5_000;

    public int MaxSeatsPerProvision { get; set; } = 50_000;
}
