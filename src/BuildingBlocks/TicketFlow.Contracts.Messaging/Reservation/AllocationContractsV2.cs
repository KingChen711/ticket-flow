namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>Price and expiry returned by a v2 hold before checkout is accepted.</summary>
public sealed record HoldQuoteV2(
    Guid HoldId,
    Guid EventId,
    IReadOnlyList<Guid> SeatIds,
    decimal Amount,
    string Currency,
    long PriceSnapshotVersion,
    DateTimeOffset ExpiresAt,
    long Generation = 0);

/// <summary>Durable allocation receipt used by later phases to make confirmation idempotent.</summary>
public sealed record ConfirmedAllocationV2(
    Guid AllocationId,
    Guid HoldId,
    Guid OrderId,
    IReadOnlyList<Guid> SeatIds,
    long Generation,
    long PriceSnapshotVersion,
    decimal Amount,
    string Currency,
    DateTimeOffset ConfirmedAt);
