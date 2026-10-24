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
    /// <summary>Allocation ownership epoch for this exact hold seat set.</summary>
    public long Generation { get; private set; }
    public HoldQuoteState State { get; private set; }
    public Guid? OrderId { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? TerminalAt { get; private set; }

    private HoldQuote() { }

    public static HoldQuote Create(
        Guid holdId,
        Guid eventId,
        Guid ownerUserId,
        IReadOnlyList<Guid> seatIds,
        decimal amount,
        string currency,
        long priceSnapshotVersion,
        long generation,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (holdId == Guid.Empty) throw new ArgumentException("Hold id is required.", nameof(holdId));
        if (eventId == Guid.Empty) throw new ArgumentException("Event id is required.", nameof(eventId));
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Owner user id is required.", nameof(ownerUserId));
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));

        if (seatIds.Count == 0 || seatIds.Any(seatId => seatId == Guid.Empty) ||
            seatIds.Distinct().Count() != seatIds.Count)
            throw new ArgumentException("Hold quote requires a non-empty exact seat set.", nameof(seatIds));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (priceSnapshotVersion <= 0) throw new ArgumentOutOfRangeException(nameof(priceSnapshotVersion));
        if (expiresAt <= createdAt) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));

        return new()
        {
            Id = holdId,
            EventId = eventId,
            OwnerUserId = ownerUserId,
            SeatIds = seatIds.ToArray(),
            Amount = amount,
            Currency = Guard.NotNullOrEmpty(currency, nameof(currency)).ToUpperInvariant(),
            PriceSnapshotVersion = priceSnapshotVersion,
            Generation = generation,
            State = HoldQuoteState.Active,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>Compatibility bridge for pre-generation callers; new production code must pass a durable generation.</summary>
    [Obsolete("Use the overload that accepts a durable allocation generation.")]
    public static HoldQuote Create(
        Guid holdId,
        Guid eventId,
        Guid ownerUserId,
        IReadOnlyList<Guid> seatIds,
        decimal amount,
        string currency,
        long priceSnapshotVersion,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) =>
        Create(
            holdId,
            eventId,
            ownerUserId,
            seatIds,
            amount,
            currency,
            priceSnapshotVersion,
            1,
            createdAt,
            expiresAt);

    public bool IsUsableBy(Guid ownerUserId, DateTimeOffset now) =>
        OwnerUserId == ownerUserId && IsActiveAt(now);

    public bool IsActiveAt(DateTimeOffset now) =>
        State == HoldQuoteState.Active && now < ExpiresAt;

    public void Consume(Guid orderId, DateTimeOffset consumedAt)
    {
        if (orderId == Guid.Empty)
            throw new ArgumentException("Order id is required.", nameof(orderId));

        if (State == HoldQuoteState.Consumed && OrderId == orderId)
            return;

        if (!IsActiveAt(consumedAt))
            throw new InvalidOperationException($"Hold quote '{Id}' is not active.");

        State = HoldQuoteState.Consumed;
        OrderId = orderId;
        TerminalAt = consumedAt;
    }

    public bool Expire(DateTimeOffset expiredAt)
    {
        if (State != HoldQuoteState.Active || expiredAt < ExpiresAt)
            return false;

        State = HoldQuoteState.Expired;
        OrderId = null;
        TerminalAt = expiredAt;
        return true;
    }

    public bool Invalidate(DateTimeOffset invalidatedAt)
    {
        if (State == HoldQuoteState.Invalidated)
            return false;

        if (State != HoldQuoteState.Active)
            throw new InvalidOperationException($"Only an active hold quote can be invalidated; current state is {State}.");

        State = HoldQuoteState.Invalidated;
        OrderId = null;
        TerminalAt = invalidatedAt;
        return true;
    }
}
