using TicketFlow.Domain;

namespace TicketFlow.Reservation.Domain.Holds;

/// <summary>Immutable server-priced quote. Its id is the Redis hold id.</summary>
public sealed class HoldQuote : Entity<Guid>
{
    public Guid EventId { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public Guid[] SeatIds { get; private set; } = [];
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public long PriceSnapshotVersion { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private HoldQuote() { }

    public static HoldQuote Create(
        Guid holdId,
        Guid eventId,
        Guid ownerUserId,
        IReadOnlyList<Guid> seatIds,
        decimal amount,
        string currency,
        long priceSnapshotVersion,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) => new()
        {
            Id = holdId,
            EventId = eventId,
            OwnerUserId = ownerUserId,
            SeatIds = seatIds.Distinct().ToArray(),
            Amount = amount,
            Currency = Guard.NotNullOrEmpty(currency, nameof(currency)).ToUpperInvariant(),
            PriceSnapshotVersion = priceSnapshotVersion,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };

    public bool IsUsableBy(Guid ownerUserId, DateTimeOffset now) =>
        OwnerUserId == ownerUserId && now < ExpiresAt;
}
