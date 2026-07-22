namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Command sent by the PurchaseSaga as a compensation step when payment or ticketing fails.
/// Phase 12 Commit 5 turns this into an owner-scoped operation: the consumer proves the exact
/// <see cref="HoldId"/>/<see cref="OrderId"/>/<see cref="OwnerUserId"/>/<see cref="Generation"/>
/// still owns each allocation before releasing it, so a delayed compensation for order A can
/// never hand back a seat that has since been resold to order B. <see cref="OperationKey"/> is
/// the durable receipt key, so a redelivery replays the original outcome rather than re-running
/// the release against whatever owns the seat now.
/// </summary>
/// <remarks>
/// <see cref="OwnerUserId"/> is optional/appended for the expand-only rollout (plan mục 10): a v1
/// payload still deserializes, and the consumer treats a missing owner as "cannot prove ownership"
/// instead of assuming it matches.
/// </remarks>
public sealed record ReleaseSeats(
    Guid OrderId,
    Guid EventId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds,
    string? OperationKey = null,
    long Generation = 0,
    Guid? OwnerUserId = null);
