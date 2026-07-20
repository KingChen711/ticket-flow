using TicketFlow.Domain;

namespace TicketFlow.Reservation.Domain.Allocations;

/// <summary>
/// Expand-only persistence model for the PostgreSQL allocation truth introduced in Phase 12.
/// Commit 2 does not write or backfill it: Redis remains the live hold implementation until the
/// later cutover commit has durable consumer state and an explicit migration procedure.
/// </summary>
public sealed class SeatAllocation : Entity<Guid>
{
    public Guid SeatId { get; private set; }
    public Guid EventId { get; private set; }
    public Guid HoldId { get; private set; }
    public Guid? OrderId { get; private set; }
    public Guid? OwnerUserId { get; private set; }
    public SeatAllocationState State { get; private set; }
    public long Generation { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public long? PriceSnapshotVersion { get; private set; }
    public decimal? Amount { get; private set; }
    public string? Currency { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private SeatAllocation() { }
}
