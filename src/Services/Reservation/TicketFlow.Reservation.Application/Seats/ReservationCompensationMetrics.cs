using System.Diagnostics.Metrics;

namespace TicketFlow.Reservation.Application.Seats;

/// <summary>
/// A stale or duplicate compensation is a silent no-op by design (plan Commit 5) — which is
/// exactly why it needs a counter: without one, the difference between "the ABA guard is working"
/// and "compensation stopped running at all" is invisible in production. Static like
/// <c>ContractVersionMetrics</c> rather than a DI singleton because the release path is entered
/// from both a consumer and an HTTP endpoint; the meter is registered for scraping in Program.cs.
/// </summary>
public static class ReservationCompensationMetrics
{
    public const string MeterName = "TicketFlow.Reservation";

    private static readonly Meter Meter = new(MeterName, "12.5.0");

    private static readonly Counter<long> Compensations =
        Meter.CreateCounter<long>(
            "ticketflow.reservation.compensation_total",
            unit: "{operation}",
            description: "Owner-scoped seat compensations, tagged by what the operation actually did.");

    private static readonly Counter<long> StaleSeats =
        Meter.CreateCounter<long>(
            "ticketflow.reservation.compensation_stale_seats_total",
            unit: "{seat}",
            description: "Seats a compensation deliberately refused to release because a newer owner holds them.");

    private static readonly Counter<long> HoldRejections =
        Meter.CreateCounter<long>(
            "ticketflow.reservation.hold_rejected_total",
            unit: "{hold}",
            description: "Hold requests refused by a per-user cap or rate limit, tagged by reason.");

    public static void RecordCompensation(string outcome) =>
        Compensations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordStaleSeats(int seatCount)
    {
        if (seatCount > 0)
            StaleSeats.Add(seatCount);
    }

    public static void RecordHoldRejection(HoldRejectionReason reason) =>
        HoldRejections.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

    public static class Outcomes
    {
        public const string Released = "released";
        public const string AlreadyReleased = "already_released";
        public const string StaleNoop = "stale_noop";
        public const string Replayed = "replayed";
        public const string Conflict = "conflict";
    }
}
