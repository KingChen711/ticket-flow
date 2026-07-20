using TicketFlow.Domain;

namespace TicketFlow.Reservation.Domain.Allocations;

/// <summary>
/// Durable operation-key receipt.  Its unique key is the idempotency boundary; processing is
/// deliberately added in a later commit with the inbox/outbox transaction.
/// </summary>
public sealed class ReservationOperationReceipt : Entity<Guid>
{
    public string OperationType { get; private set; } = null!;
    public string BusinessKey { get; private set; } = null!;
    public string? Outcome { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private ReservationOperationReceipt() { }
}
