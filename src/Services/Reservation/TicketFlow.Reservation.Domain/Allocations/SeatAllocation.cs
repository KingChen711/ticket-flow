using TicketFlow.Domain;

namespace TicketFlow.Reservation.Domain.Allocations;

/// <summary>PostgreSQL source of truth for the current allocation of one event seat.</summary>
public sealed class SeatAllocation : Entity<Guid>
{
    public Guid SeatId { get; private set; }
    public Guid EventId { get; private set; }
    public Guid HoldId { get; private set; }
    public Guid? OrderId { get; private set; }
    public Guid? OwnerUserId { get; private set; }
    public SeatAllocationState State { get; private set; }

    public void Release(Guid holdId, DateTimeOffset releasedAt)
    {
        if (holdId == Guid.Empty)
        {
            throw new ArgumentException("Hold id is required.", nameof(holdId));
        }

        if (HoldId != holdId)
        {
            throw new InvalidOperationException("The allocation belongs to a different hold.");
        }

        if (State == SeatAllocationState.Released)
        {
            return;
        }

        if (State != SeatAllocationState.Held)
        {
            throw new InvalidOperationException($"Only held allocations can be released; current state is {State}.");
        }

        State = SeatAllocationState.Released;
        ExpiresAt = releasedAt;
    }
    /// <summary>
    /// Ownership epoch shared by every seat in one hold. It is used for allocation CAS/ABA
    /// protection; it is not a global event, SignalR, or aggregate revision.
    /// </summary>
    public long Generation { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public long? PriceSnapshotVersion { get; private set; }
    public decimal? Amount { get; private set; }
    public string? Currency { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private SeatAllocation() { }

    public static SeatAllocation CreateConfirmed(
        Guid seatId,
        Guid eventId,
        Guid holdId,
        Guid orderId,
        Guid ownerUserId,
        long generation,
        long priceSnapshotVersion,
        decimal amount,
        string currency,
        DateTimeOffset now)
    {
        ValidateConfirmedValues(
            seatId,
            eventId,
            holdId,
            orderId,
            ownerUserId,
            generation,
            priceSnapshotVersion,
            amount,
            currency);

        return new SeatAllocation
        {
            Id = Guid.NewGuid(),
            SeatId = seatId,
            EventId = eventId,
            HoldId = holdId,
            OrderId = orderId,
            OwnerUserId = ownerUserId,
            State = SeatAllocationState.Confirmed,
            Generation = generation,
            ExpiresAt = null,
            PriceSnapshotVersion = priceSnapshotVersion,
            // This is the total immutable quote amount, repeated on every seat row belonging to
            // the exact allocation set. Consumers must use the receipt/quote once, not sum rows.
            Amount = amount,
            Currency = currency.ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void ReallocateConfirmed(
        Guid holdId,
        Guid orderId,
        Guid ownerUserId,
        long generation,
        long priceSnapshotVersion,
        decimal amount,
        string currency,
        DateTimeOffset now)
    {
        ValidateConfirmedValues(
            SeatId,
            EventId,
            holdId,
            orderId,
            ownerUserId,
            generation,
            priceSnapshotVersion,
            amount,
            currency);

        if (State == SeatAllocationState.Confirmed)
        {
            if (HoldId == holdId && OrderId == orderId && OwnerUserId == ownerUserId && Generation == generation)
                return;

            throw new InvalidOperationException($"Seat '{SeatId}' is already confirmed by another allocation.");
        }

        if (State == SeatAllocationState.Held &&
            (HoldId != holdId || OwnerUserId != ownerUserId || Generation != generation))
        {
            if (ExpiresAt > now)
                throw new InvalidOperationException($"Seat '{SeatId}' is held by another allocation.");

            if (generation <= Generation)
            {
                throw new InvalidOperationException(
                    $"Generation '{generation}' must be greater than current generation '{Generation}'.");
            }
        }

        if (State is not SeatAllocationState.Held && generation <= Generation)
        {
            throw new InvalidOperationException(
                $"Generation '{generation}' must be greater than current generation '{Generation}'.");
        }

        HoldId = holdId;
        OrderId = orderId;
        OwnerUserId = ownerUserId;
        State = SeatAllocationState.Confirmed;
        Generation = generation;
        ExpiresAt = null;
        PriceSnapshotVersion = priceSnapshotVersion;
        Amount = amount;
        Currency = currency.ToUpperInvariant();
        UpdatedAt = now;
    }

    public static SeatAllocation CreateHeld(
        Guid seatId,
        Guid eventId,
        Guid holdId,
        Guid ownerUserId,
        long generation,
        DateTimeOffset expiresAt,
        long priceSnapshotVersion,
        decimal amount,
        string currency,
        DateTimeOffset now)
    {
        ValidateHeldValues(
            seatId,
            eventId,
            holdId,
            ownerUserId,
            generation,
            expiresAt,
            priceSnapshotVersion,
            amount,
            currency,
            now);

        return new SeatAllocation
        {
            Id = Guid.NewGuid(),
            SeatId = seatId,
            EventId = eventId,
            HoldId = holdId,
            OwnerUserId = ownerUserId,
            State = SeatAllocationState.Held,
            Generation = generation,
            ExpiresAt = expiresAt,
            PriceSnapshotVersion = priceSnapshotVersion,
            Amount = amount,
            Currency = currency.ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Rehold(
        Guid holdId,
        Guid ownerUserId,
        long generation,
        DateTimeOffset expiresAt,
        long priceSnapshotVersion,
        decimal amount,
        string currency,
        DateTimeOffset now)
    {
        if (State == SeatAllocationState.Confirmed)
            throw new InvalidOperationException($"Seat '{SeatId}' is already confirmed.");

        if (State == SeatAllocationState.Held && ExpiresAt > now)
            throw new InvalidOperationException($"Seat '{SeatId}' is held until '{ExpiresAt:O}'.");

        if (generation <= Generation)
            throw new InvalidOperationException(
                $"Generation '{generation}' must be greater than current generation '{Generation}'.");

        ValidateHeldValues(
            SeatId,
            EventId,
            holdId,
            ownerUserId,
            generation,
            expiresAt,
            priceSnapshotVersion,
            amount,
            currency,
            now);

        HoldId = holdId;
        OrderId = null;
        OwnerUserId = ownerUserId;
        State = SeatAllocationState.Held;
        Generation = generation;
        ExpiresAt = expiresAt;
        PriceSnapshotVersion = priceSnapshotVersion;
        Amount = amount;
        Currency = currency.ToUpperInvariant();
        UpdatedAt = now;
    }

    public void Confirm(
        Guid orderId,
        Guid expectedHoldId,
        Guid expectedOwnerUserId,
        long expectedGeneration,
        DateTimeOffset now)
    {
        if (State == SeatAllocationState.Confirmed &&
            OrderId == orderId &&
            HoldId == expectedHoldId &&
            OwnerUserId == expectedOwnerUserId &&
            Generation == expectedGeneration)
            return;

        if (State != SeatAllocationState.Held)
            throw new InvalidOperationException($"Seat '{SeatId}' is not held.");

        if (HoldId != expectedHoldId ||
            OwnerUserId != expectedOwnerUserId ||
            Generation != expectedGeneration)
            throw new InvalidOperationException($"Allocation authority mismatch for seat '{SeatId}'.");

        if (ExpiresAt is null || ExpiresAt <= now)
            throw new InvalidOperationException($"Hold '{HoldId}' has expired for seat '{SeatId}'.");

        if (orderId == Guid.Empty)
            throw new ArgumentException("Order id cannot be empty.", nameof(orderId));

        OrderId = orderId;
        State = SeatAllocationState.Confirmed;
        ExpiresAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Owner-scoped compensation (Phase 12 Commit 5). The caller must prove the exact
    /// hold/order/owner/generation that this row still carries; anything else is a stale or
    /// duplicate compensation whose seat has already moved on, and it is refused as a no-op
    /// rather than handed back to the pool. This is the ABA guard: order A's delayed release
    /// arriving after the seat was resold to order B must not free B's seat.
    /// </summary>
    public SeatAllocationReleaseOutcome ReleaseOwned(
        Guid expectedHoldId,
        Guid? expectedOrderId,
        Guid expectedOwnerUserId,
        long expectedGeneration,
        DateTimeOffset now)
    {
        if (expectedHoldId == Guid.Empty)
            throw new ArgumentException("Hold id is required.", nameof(expectedHoldId));
        if (expectedOwnerUserId == Guid.Empty)
            throw new ArgumentException("Owner user id is required.", nameof(expectedOwnerUserId));
        if (expectedGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));

        if (HoldId != expectedHoldId ||
            OwnerUserId != expectedOwnerUserId ||
            Generation != expectedGeneration)
            return SeatAllocationReleaseOutcome.OwnershipChanged;

        // A confirmed row also has to match the order that owns it. A release naming a different
        // order for the same hold is the compensation of a different business operation.
        if (State == SeatAllocationState.Confirmed && expectedOrderId is not null && OrderId != expectedOrderId)
            return SeatAllocationReleaseOutcome.OwnershipChanged;

        if (State is SeatAllocationState.Released or SeatAllocationState.Expired)
            return SeatAllocationReleaseOutcome.AlreadyReleased;

        State = SeatAllocationState.Released;
        ExpiresAt = now;
        UpdatedAt = now;
        return SeatAllocationReleaseOutcome.Released;
    }

    public void MarkExpired(DateTimeOffset now)
    {
        if (State != SeatAllocationState.Held || ExpiresAt is null || ExpiresAt > now)
            return;

        State = SeatAllocationState.Expired;
        UpdatedAt = now;
    }

    public bool IsActiveAt(DateTimeOffset now) =>
        State == SeatAllocationState.Confirmed ||
        State == SeatAllocationState.Held && ExpiresAt > now;

    private static void ValidateHeldValues(
        Guid seatId,
        Guid eventId,
        Guid holdId,
        Guid ownerUserId,
        long generation,
        DateTimeOffset expiresAt,
        long priceSnapshotVersion,
        decimal amount,
        string currency,
        DateTimeOffset now)
    {
        if (seatId == Guid.Empty) throw new ArgumentException("Seat id cannot be empty.", nameof(seatId));
        if (eventId == Guid.Empty) throw new ArgumentException("Event id cannot be empty.", nameof(eventId));
        if (holdId == Guid.Empty) throw new ArgumentException("Hold id cannot be empty.", nameof(holdId));
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Owner user id cannot be empty.", nameof(ownerUserId));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (expiresAt <= now) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        if (priceSnapshotVersion <= 0) throw new ArgumentOutOfRangeException(nameof(priceSnapshotVersion));
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));
    }

    private static void ValidateConfirmedValues(
        Guid seatId,
        Guid eventId,
        Guid holdId,
        Guid orderId,
        Guid ownerUserId,
        long generation,
        long priceSnapshotVersion,
        decimal amount,
        string currency)
    {
        if (seatId == Guid.Empty) throw new ArgumentException("Seat id cannot be empty.", nameof(seatId));
        if (eventId == Guid.Empty) throw new ArgumentException("Event id cannot be empty.", nameof(eventId));
        if (holdId == Guid.Empty) throw new ArgumentException("Hold id cannot be empty.", nameof(holdId));
        if (orderId == Guid.Empty) throw new ArgumentException("Order id cannot be empty.", nameof(orderId));
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Owner user id cannot be empty.", nameof(ownerUserId));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (priceSnapshotVersion <= 0) throw new ArgumentOutOfRangeException(nameof(priceSnapshotVersion));
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));
    }
}
