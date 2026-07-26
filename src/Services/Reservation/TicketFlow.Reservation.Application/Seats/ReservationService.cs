using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Allocations;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Seats;

public sealed class ReservationService(
    ISeatRepository seatRepository,
    ISeatHoldStore holdStore,
    IEventSalePolicyRepository salePolicyRepository,
    IHoldQuoteRepository holdQuoteRepository,
    ISeatAllocationRepository allocationRepository,
    IReservationOperationReceiptRepository receiptRepository,
    IAllocationGenerationSource generationSource,
    IUnitOfWork unitOfWork,
    ISeatAvailabilityEventPublisher availabilityEventPublisher,
    TimeProvider timeProvider,
    ReservationOptions options)
{
    private readonly TimeSpan _holdTtl = TimeSpan.FromMinutes(options.HoldTtlMinutes);

    public async Task<HoldSeatsResult> HoldAsync(
        HoldSeatsRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateHoldRequest(request);

        // Concurrent holds share this lock, while a Catalog lifecycle update takes it
        // exclusively. A cancellation therefore cannot miss a quote inserted from a stale
        // Published snapshot between its active-quote scan and commit.
        await using var salePolicyLock = await receiptRepository.AcquireSharedLockAsync(
            "sale-policy",
            request.EventId.ToString("N"),
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        var policy = await salePolicyRepository.GetAsync(request.EventId, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{request.EventId}' has no sale policy snapshot.");

        if (!policy.IsSaleableAt(now))
            throw new InvalidOperationException($"Event '{request.EventId}' is not currently saleable.");

        // Budget is checked before any seat work so a throttled caller learns nothing about which
        // seats are free — and pays no Redis/DB cost for a request that cannot succeed.
        var budgetRejection = await CheckHoldBudgetAsync(request, now, cancellationToken);
        if (budgetRejection is not null)
            return budgetRejection;

        var seats = await seatRepository.GetByIdsAsync(request.EventId, request.SeatIds, cancellationToken);
        if (seats.Count != request.SeatIds.Count)
            throw new InvalidOperationException("One or more seats do not belong to this event.");

        var durableAllocations = await allocationRepository.GetBySeatIdsAsync(
            request.EventId,
            request.SeatIds,
            cancellationToken);
        var durablyUnavailable = durableAllocations
            .Where(allocation => allocation.IsActiveAt(now))
            .Select(allocation => allocation.SeatId)
            .ToHashSet();
        durablyUnavailable.UnionWith(
            seats.Where(seat => seat.Status == SeatStatus.Sold).Select(seat => seat.Id));

        var activeQuotes = await holdQuoteRepository.GetActiveOverlappingAsync(
            request.EventId,
            request.SeatIds,
            now,
            cancellationToken);
        foreach (var activeQuote in activeQuotes)
        {
            foreach (var seatId in activeQuote.SeatIds)
            {
                if (request.SeatIds.Contains(seatId))
                    durablyUnavailable.Add(seatId);
            }
        }

        if (durablyUnavailable.Count > 0)
            return HoldSeatsResult.Unavailable(durablyUnavailable.Order().ToArray());

        var pricesBySection = policy.PriceTiers.ToDictionary(tier => tier.SectionId);
        var prices = seats.Select(seat => pricesBySection.TryGetValue(seat.SectionId, out var price)
                ? price
                : throw new InvalidOperationException(
                    $"Section '{seat.SectionId}' has no price in snapshot {policy.PriceSnapshotVersion}."))
            .ToList();

        var currencies = prices.Select(price => price.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currencies.Count != 1)
            throw new InvalidOperationException("A hold cannot span price tiers with different currencies.");

        var expiresAt = now.Add(_holdTtl) < policy.SaleEndsAt
            ? now.Add(_holdTtl)
            : policy.SaleEndsAt;
        var ttl = expiresAt - now;
        if (ttl <= TimeSpan.Zero)
            throw new InvalidOperationException($"Event '{request.EventId}' is no longer saleable.");

        var holdId = Guid.NewGuid();
        var generation = await generationSource.NextAsync(cancellationToken);
        var cacheRequest = new HoldStoreRequest(
            request.EventId,
            request.SeatIds,
            holdId,
            request.OwnerUserId,
            generation,
            ttl);
        var holdResult = await holdStore.TryHoldAsync(cacheRequest, cancellationToken);

        if (holdResult.Outcome != HoldStoreOutcome.Held)
            return HoldSeatsResult.Unavailable(holdResult.UnavailableSeatIds);

        var quote = HoldQuote.Create(
            holdId,
            request.EventId,
            request.OwnerUserId,
            request.SeatIds,
            prices.Sum(price => price.Amount),
            currencies[0],
            policy.PriceSnapshotVersion,
            generation,
            now,
            expiresAt);

        try
        {
            // An active quote is the durable Held authority. SeatAllocation rows are deliberately
            // not created until ConfirmSeats consumes this quote.
            holdQuoteRepository.Add(quote);
            await availabilityEventPublisher.PublishAsync(
                request.EventId,
                request.SeatIds,
                "Held",
                generation,
                now,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await holdStore.ReleaseAsync(
                new ReleaseHoldStoreRequest(
                    request.EventId,
                    holdId,
                    request.OwnerUserId,
                    generation,
                    request.SeatIds),
                CancellationToken.None);
            throw;
        }

        return HoldSeatsResult.Succeeded(MapQuote(quote));
    }

    public async Task<HoldQuoteResponse?> GetQuoteAsync(
        Guid holdId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var quote = await holdQuoteRepository.GetAsync(holdId, cancellationToken);
        if (quote is null || !quote.IsUsableBy(ownerUserId, timeProvider.GetUtcNow()))
            return null;

        var policy = await salePolicyRepository.GetAsync(quote.EventId, cancellationToken);
        if (policy is null || !string.Equals(policy.Status, "Published", StringComparison.OrdinalIgnoreCase))
            return null;

        return MapQuote(quote);
    }

    public async Task<ConfirmSeatAllocationResult> ConfirmAsync(
        ConfirmSeatAllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConfirmRequest(request);

        var businessKey = ReservationOperationReceipt.ConfirmBusinessKey(
            request.OperationKey,
            request.OrderId,
            request.HoldId);
        var fingerprint = ConfirmRequestFingerprint.Create(request);

        // Always acquire order before hold. The order lock prevents two different holds from
        // mutating Redis for the same order, while the hold lock serializes competing operations
        // against one quote. MassTransit's EF outbox supplies the ambient transaction in production.
        await using var orderLock = await receiptRepository.AcquireLockAsync(
            "confirm-order",
            request.OrderId.ToString("N"),
            cancellationToken);
        await using var authorityLock = await receiptRepository.AcquireLockAsync(
            "confirm-hold",
            request.HoldId.ToString("N"),
            cancellationToken);

        var receipt = await receiptRepository.GetAsync(
            ReservationOperationReceipt.OperationTypes.ConfirmSeats,
            businessKey,
            cancellationToken);

        if (receipt is null)
        {
            receipt = ReservationOperationReceipt.StartConfirm(
                businessKey,
                fingerprint,
                timeProvider.GetUtcNow());
            receiptRepository.Add(receipt);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (!receipt.IsPending || !receipt.HasFingerprint(fingerprint))
        {
            try
            {
                return MapReceipt(receipt.ReplayConfirm(fingerprint));
            }
            catch (ReservationOperationFingerprintMismatchException)
            {
                return IdempotencyConflict(request, receipt.Id);
            }
        }

        var now = timeProvider.GetUtcNow();
        var existingOrderQuote = await holdQuoteRepository.GetByOrderAsync(
            request.OrderId,
            cancellationToken);
        if (existingOrderQuote is not null && existingOrderQuote.Id != request.HoldId)
        {
            return await CompleteConfirmFailureAsync(
                receipt,
                request,
                fingerprint,
                now,
                "OrderAlreadyAllocated",
                "The order is already mapped to a different authoritative hold quote.",
                cancellationToken);
        }

        var quote = await holdQuoteRepository.GetAsync(request.HoldId, cancellationToken);
        if (quote is null)
        {
            return await CompleteConfirmFailureAsync(
                receipt,
                request,
                fingerprint,
                now,
                "HoldNotFound",
                "The authoritative hold quote was not found.",
                cancellationToken);
        }

        var authoritativeSeatIds = quote.SeatIds.Order().ToArray();
        if (!MatchesAuthority(quote, request, authoritativeSeatIds))
        {
            return await CompleteConfirmFailureAsync(
                receipt,
                request,
                fingerprint,
                now,
                "AuthorityMismatch",
                "Confirm payload does not match the authoritative hold quote.",
                cancellationToken);
        }

        if (quote.State == HoldQuoteState.Consumed)
        {
            if (quote.OrderId != request.OrderId)
            {
                return await CompleteConfirmFailureAsync(
                    receipt,
                    request,
                    fingerprint,
                    now,
                    "HoldAlreadyConsumed",
                    "The hold quote was already consumed by another order.",
                    cancellationToken);
            }

            var consumedAllocations = await allocationRepository.GetByHoldAsync(quote.Id, cancellationToken);
            if (!IsExactConfirmedAllocationSet(consumedAllocations, quote, request.OrderId, authoritativeSeatIds))
                throw new InvalidOperationException($"Consumed hold quote '{quote.Id}' has an ambiguous allocation set.");

            return await CompleteConfirmSuccessAsync(
                receipt,
                request,
                quote,
                authoritativeSeatIds,
                fingerprint,
                now,
                publishAvailability: false,
                cancellationToken);
        }

        if (quote.State is HoldQuoteState.Invalidated or HoldQuoteState.Expired)
        {
            return await CompleteConfirmFailureAsync(
                receipt,
                request,
                fingerprint,
                now,
                quote.State == HoldQuoteState.Expired ? "HoldExpired" : "HoldInvalidated",
                $"The hold quote is {quote.State.ToString().ToLowerInvariant()}.",
                cancellationToken);
        }

        if (quote.ExpiresAt <= now)
        {
            quote.Expire(now);
            await holdStore.ReleaseAsync(ToReleaseRequest(quote), cancellationToken);
            await availabilityEventPublisher.PublishAsync(
                quote.EventId,
                authoritativeSeatIds,
                "Available",
                quote.Generation,
                now,
                cancellationToken);
            return await CompleteConfirmFailureAsync(
                receipt,
                request,
                fingerprint,
                now,
                "HoldExpired",
                "The hold expired before confirmation.",
                cancellationToken);
        }

        var policy = await salePolicyRepository.GetAsync(quote.EventId, cancellationToken);
        if (policy is null || !string.Equals(policy.Status, "Published", StringComparison.OrdinalIgnoreCase))
        {
            await holdStore.ReleaseAsync(ToReleaseRequest(quote), cancellationToken);
            quote.Invalidate(now);
            await availabilityEventPublisher.PublishAsync(
                quote.EventId,
                authoritativeSeatIds,
                "Available",
                quote.Generation,
                now,
                cancellationToken);
            return await CompleteConfirmFailureAsync(
                receipt,
                request,
                fingerprint,
                now,
                "EventNotSaleable",
                "The event is no longer saleable.",
                cancellationToken);
        }

        var existingAllocations = await allocationRepository.GetBySeatIdsAsync(
            quote.EventId,
            authoritativeSeatIds,
            cancellationToken);
        if (HasAllocationConflict(existingAllocations, quote, request.OrderId, now))
        {
            return await CompleteConfirmFailureAsync(
                receipt,
                request,
                fingerprint,
                now,
                "AllocationAuthorityConflict",
                "A seat already has a conflicting durable allocation.",
                cancellationToken);
        }

        var cacheRequest = new ConfirmHoldStoreRequest(
            quote.EventId,
            quote.Id,
            request.OrderId,
            quote.OwnerUserId,
            quote.Generation,
            authoritativeSeatIds);
        var cacheResult = await holdStore.ConfirmAsync(cacheRequest, cancellationToken);

        if (cacheResult.Outcome == ConfirmHoldStoreOutcome.MissingOrExpired)
        {
            // Redis is recoverable cache: restore Held from the still-active quote, then retry
            // the exact owner/generation/seat-set CAS.
            foreach (var seatId in authoritativeSeatIds)
            {
                await holdStore.RestoreAsync(
                    new RestoreSeatAllocationRequest(
                        quote.EventId,
                        new CachedSeatAllocation(
                            seatId,
                            quote.Id,
                            quote.OwnerUserId,
                            quote.Generation,
                            CachedAllocationState.Held,
                            null,
                            quote.ExpiresAt)),
                    cancellationToken);
            }

            cacheResult = await holdStore.ConfirmAsync(cacheRequest, cancellationToken);
        }

        if (!cacheResult.IsSuccessful)
        {
            var code = cacheResult.Outcome == ConfirmHoldStoreOutcome.Conflict
                ? "CacheAuthorityConflict"
                : "HoldMissingOrExpired";
            return await CompleteConfirmFailureAsync(
                receipt,
                request,
                fingerprint,
                now,
                code,
                cacheResult.Reason ?? "The cache could not confirm the authoritative hold.",
                cancellationToken);
        }

        if (!cacheResult.SeatIds.Order().SequenceEqual(authoritativeSeatIds))
            throw new InvalidOperationException("Cache confirmation returned a non-authoritative seat set.");

        var seats = await seatRepository.GetByIdsAsync(quote.EventId, authoritativeSeatIds, cancellationToken);
        if (seats.Count != authoritativeSeatIds.Length)
            throw new InvalidOperationException("One or more allocated seats are missing from inventory.");

        var allocationsBySeat = existingAllocations.ToDictionary(allocation => allocation.SeatId);
        var newAllocations = new List<SeatAllocation>();
        foreach (var seatId in authoritativeSeatIds)
        {
            if (allocationsBySeat.TryGetValue(seatId, out var allocation))
            {
                allocation.ReallocateConfirmed(
                    quote.Id,
                    request.OrderId,
                    quote.OwnerUserId,
                    quote.Generation,
                    quote.PriceSnapshotVersion,
                    quote.Amount,
                    quote.Currency,
                    now);
            }
            else
            {
                newAllocations.Add(SeatAllocation.CreateConfirmed(
                    seatId,
                    quote.EventId,
                    quote.Id,
                    request.OrderId,
                    quote.OwnerUserId,
                    quote.Generation,
                    quote.PriceSnapshotVersion,
                    quote.Amount,
                    quote.Currency,
                    now));
            }
        }

        allocationRepository.AddRange(newAllocations);
        foreach (var seat in seats.Where(seat => seat.Status != SeatStatus.Sold))
            seat.MarkSold();

        quote.Consume(request.OrderId, now);
        return await CompleteConfirmSuccessAsync(
            receipt,
            request,
            quote,
            authoritativeSeatIds,
            fingerprint,
            now,
            publishAvailability: true,
            cancellationToken);
    }

    public async Task<bool> ReleaseOwnedHoldAsync(
        Guid holdId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var authorityLock = await receiptRepository.AcquireLockAsync(
            "confirm-hold",
            holdId.ToString("N"),
            cancellationToken);
        var quote = await holdQuoteRepository.GetAsync(holdId, cancellationToken);
        if (quote is null || quote.OwnerUserId != ownerUserId)
            return false;

        return await ReleaseQuoteAsync(quote, cancellationToken);
    }

    /// <summary>
    /// Owner-scoped compensation (Commit 5). Replaces the old <c>ReleaseAsync + UnsellAsync</c>
    /// pair, which took only event/hold/seat ids and therefore could not tell "my seats" from
    /// "seats that were resold to someone else while this message sat in a queue". Everything the
    /// caller asserts is treated as an expectation checked against the durable quote/allocation;
    /// whatever no longer matches is reported as stale and left alone.
    /// </summary>
    public async Task<ReleaseSeatAllocationResult> CompensateAsync(
        ReleaseSeatAllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateReleaseRequest(request);

        var businessKey = ReservationOperationReceipt.ReleaseBusinessKey(
            request.OperationKey,
            request.OrderId,
            request.HoldId);
        var fingerprint = ReleaseRequestFingerprint.Create(request);

        // Same order → hold lock ordering as ConfirmAsync, so a confirm and a compensation for the
        // same order can never interleave halfway through each other's cache/DB writes.
        await using var orderLock = await receiptRepository.AcquireLockAsync(
            "confirm-order",
            request.OrderId.ToString("N"),
            cancellationToken);
        await using var authorityLock = await receiptRepository.AcquireLockAsync(
            "confirm-hold",
            request.HoldId.ToString("N"),
            cancellationToken);

        var receipt = await receiptRepository.GetAsync(
            ReservationOperationReceipt.OperationTypes.ReleaseSeats,
            businessKey,
            cancellationToken);

        if (receipt is null)
        {
            receipt = ReservationOperationReceipt.StartRelease(
                businessKey,
                fingerprint,
                timeProvider.GetUtcNow());
            receiptRepository.Add(receipt);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (!receipt.IsPending || !receipt.HasFingerprint(fingerprint))
        {
            try
            {
                var replayed = MapReleaseReceipt(receipt.ReplayRelease(fingerprint));
                ReservationCompensationMetrics.RecordCompensation(
                    ReservationCompensationMetrics.Outcomes.Replayed);
                return replayed;
            }
            catch (ReservationOperationFingerprintMismatchException)
            {
                ReservationCompensationMetrics.RecordCompensation(
                    ReservationCompensationMetrics.Outcomes.Conflict);
                return ReleaseIdempotencyConflict(request, receipt.Id);
            }
        }

        var now = timeProvider.GetUtcNow();
        var quote = await holdQuoteRepository.GetAsync(request.HoldId, cancellationToken);
        if (quote is null)
        {
            return await CompleteReleaseAsync(
                receipt, request, fingerprint, now, [], [], false,
                "HoldNotFound",
                "No durable hold quote exists for this compensation.",
                ReservationCompensationMetrics.Outcomes.Conflict,
                cancellationToken);
        }

        // The quote — not the message — is the authority. A message that disagrees about event,
        // owner, generation or seat set is exactly the "hold A, compensate B" shape, so it must
        // not be allowed to release anything.
        if (quote.EventId != request.EventId ||
            request.OwnerUserId is not null && quote.OwnerUserId != request.OwnerUserId ||
            request.Generation > 0 && quote.Generation != request.Generation ||
            request.SeatIds.Count > 0 && !quote.SeatIds.Order().SequenceEqual(request.SeatIds.Order()))
        {
            return await CompleteReleaseAsync(
                receipt, request, fingerprint, now, [], quote.SeatIds, false,
                "AuthorityMismatch",
                "The compensation payload does not match the authoritative hold quote.",
                ReservationCompensationMetrics.Outcomes.Conflict,
                cancellationToken);
        }

        // Consumed by a different order: this hold's seats were sold to someone else, so there is
        // nothing this order may take back. A defined no-op, not a failure.
        if (quote.State == HoldQuoteState.Consumed && quote.OrderId != request.OrderId)
        {
            return await CompleteReleaseAsync(
                receipt, request, fingerprint, now, [], quote.SeatIds, true,
                "HoldOwnedByAnotherOrder",
                "The hold quote was consumed by a different order; nothing was released.",
                ReservationCompensationMetrics.Outcomes.StaleNoop,
                cancellationToken);
        }

        if (quote.State == HoldQuoteState.Consumed)
            return await CompensateConfirmedAsync(receipt, request, quote, fingerprint, now, cancellationToken);

        return await CompensateHeldAsync(receipt, request, quote, fingerprint, now, cancellationToken);
    }

    /// <summary>Reverses a confirmed sale, seat by seat, only where this order still owns it.</summary>
    private async Task<ReleaseSeatAllocationResult> CompensateConfirmedAsync(
        ReservationOperationReceipt receipt,
        ReleaseSeatAllocationRequest request,
        HoldQuote quote,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authoritativeSeatIds = quote.SeatIds.Order().ToArray();
        var allocations = await allocationRepository.GetByHoldAsync(quote.Id, cancellationToken);
        var allocationsBySeat = allocations.ToDictionary(allocation => allocation.SeatId);

        var releasedSeatIds = new List<Guid>();
        var staleSeatIds = new List<Guid>();
        foreach (var seatId in authoritativeSeatIds)
        {
            if (!allocationsBySeat.TryGetValue(seatId, out var allocation))
            {
                // Consumed quote without a row for one of its seats is ambiguous, never freed.
                staleSeatIds.Add(seatId);
                continue;
            }

            var outcome = allocation.ReleaseOwned(
                quote.Id,
                request.OrderId,
                quote.OwnerUserId,
                quote.Generation,
                now);
            switch (outcome)
            {
                case SeatAllocationReleaseOutcome.Released:
                case SeatAllocationReleaseOutcome.AlreadyReleased:
                    releasedSeatIds.Add(seatId);
                    break;
                default:
                    staleSeatIds.Add(seatId);
                    break;
            }
        }

        if (releasedSeatIds.Count > 0)
        {
            var cacheResult = await holdStore.ReleaseAllocationAsync(
                new ReleaseAllocationStoreRequest(
                    quote.EventId,
                    quote.Id,
                    request.OrderId,
                    quote.OwnerUserId,
                    quote.Generation,
                    releasedSeatIds),
                cancellationToken);

            // The cache can know about an ownership change the durable rows have not seen yet
            // (a rebuild in flight, a newer confirm mid-transaction). Two authorities disagreeing
            // about who owns a seat is exactly the case that must NOT be auto-resolved here:
            // throwing rolls back this transaction — including the pending receipt — so the
            // message is retried against a consistent view instead of freeing a contested seat.
            if (cacheResult.StaleSeatIds.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Redis reports {cacheResult.StaleSeatIds.Count} seat(s) of hold '{quote.Id}' are owned by a " +
                    "newer allocation while PostgreSQL still assigns them to this order; " +
                    "the durable release was rolled back for reconciliation.");
            }

            var seats = await seatRepository.GetByIdsAsync(quote.EventId, releasedSeatIds, cancellationToken);
            foreach (var seat in seats)
                seat.MarkAvailable();

            // Commit 5 delta: the old UnsellAsync never notified, so a compensated seat stayed
            // visually Sold on every open seat map until a manual refetch. Availability now goes
            // through the same outbox-backed publisher as hold/confirm.
            await availabilityEventPublisher.PublishAsync(
                quote.EventId,
                releasedSeatIds,
                "Available",
                quote.Generation,
                now,
                cancellationToken);
        }

        var outcomeName = staleSeatIds.Count > 0
            ? ReservationCompensationMetrics.Outcomes.StaleNoop
            : releasedSeatIds.Count > 0
                ? ReservationCompensationMetrics.Outcomes.Released
                : ReservationCompensationMetrics.Outcomes.AlreadyReleased;

        return await CompleteReleaseAsync(
            receipt, request, fingerprint, now, releasedSeatIds, staleSeatIds, true,
            staleSeatIds.Count > 0 ? "PartiallyStale" : null,
            staleSeatIds.Count > 0
                ? "One or more seats are owned by a newer allocation and were left untouched."
                : null,
            outcomeName,
            cancellationToken);
    }

    /// <summary>Compensating an order whose hold was never confirmed: release the quote itself.</summary>
    private async Task<ReleaseSeatAllocationResult> CompensateHeldAsync(
        ReservationOperationReceipt receipt,
        ReleaseSeatAllocationRequest request,
        HoldQuote quote,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var seatIds = quote.SeatIds.Order().ToArray();
        if (quote.State is HoldQuoteState.Invalidated or HoldQuoteState.Expired)
        {
            return await CompleteReleaseAsync(
                receipt, request, fingerprint, now, seatIds, [], true,
                null, null,
                ReservationCompensationMetrics.Outcomes.AlreadyReleased,
                cancellationToken);
        }

        var releaseResult = await holdStore.ReleaseAsync(ToReleaseRequest(quote), cancellationToken);
        if (!releaseResult.IsSuccessful)
        {
            return await CompleteReleaseAsync(
                receipt, request, fingerprint, now, [], seatIds, true,
                "CacheAuthorityConflict",
                releaseResult.Reason ?? "The cached hold is owned by a newer allocation.",
                ReservationCompensationMetrics.Outcomes.StaleNoop,
                cancellationToken);
        }

        if (quote.ExpiresAt <= now)
            quote.Expire(now);
        else
            quote.Invalidate(now);

        await availabilityEventPublisher.PublishAsync(
            quote.EventId,
            seatIds,
            "Available",
            quote.Generation,
            now,
            cancellationToken);

        return await CompleteReleaseAsync(
            receipt, request, fingerprint, now, seatIds, [], true,
            null, null,
            ReservationCompensationMetrics.Outcomes.Released,
            cancellationToken);
    }

    private async Task<ReleaseSeatAllocationResult> CompleteReleaseAsync(
        ReservationOperationReceipt receipt,
        ReleaseSeatAllocationRequest request,
        string fingerprint,
        DateTimeOffset now,
        IReadOnlyList<Guid> releasedSeatIds,
        IReadOnlyList<Guid> staleSeatIds,
        bool succeeded,
        string? failureCode,
        string? failureReason,
        string metricOutcome,
        CancellationToken cancellationToken)
    {
        var snapshot = new ReleaseSeatsReceiptSnapshot(
            succeeded,
            request.OrderId,
            request.EventId,
            request.HoldId,
            request.OwnerUserId,
            request.Generation,
            releasedSeatIds.Order().ToArray(),
            staleSeatIds.Order().ToArray(),
            Guid.Empty,
            now,
            failureCode,
            failureReason);
        receipt.CompletePendingRelease(fingerprint, snapshot, now);

        // Allocation state, seat status, the receipt and the outbox message commit together.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        ReservationCompensationMetrics.RecordCompensation(metricOutcome);
        ReservationCompensationMetrics.RecordStaleSeats(staleSeatIds.Count);
        return MapReleaseReceipt(receipt.ReplayRelease(fingerprint));
    }

    public async Task<EventAvailabilityResponse> GetAvailabilityAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var seats = await seatRepository.GetByEventAsync(eventId, cancellationToken);
        var liveStatuses = await holdStore.GetLiveStatusesAsync(
            eventId,
            seats.Select(seat => seat.Id).ToList(),
            cancellationToken);
        var allocations = await allocationRepository.GetBySeatIdsAsync(
            eventId,
            seats.Select(seat => seat.Id).ToList(),
            cancellationToken);
        var allocationBySeat = allocations.ToDictionary(allocation => allocation.SeatId);

        var dtos = seats
            .Select(seat =>
            {
                var status = seat.Status == SeatStatus.Sold
                    ? SeatStatus.Sold
                    : liveStatuses.TryGetValue(seat.Id, out var live) ? live : seat.Status;
                allocationBySeat.TryGetValue(seat.Id, out var allocation);
                return new SeatDto(
                    seat.Id,
                    seat.SectionId,
                    seat.Label,
                    status.ToString(),
                    allocation?.Generation ?? 0,
                    allocation?.UpdatedAt);
            })
            .ToList();

        return new EventAvailabilityResponse(eventId, dtos);
    }

    public async Task<CheckAvailabilityResult> CheckAvailabilityAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return new CheckAvailabilityResult(true, []);

        var knownSeats = await seatRepository.GetByIdsAsync(eventId, seatIds, cancellationToken);
        var knownIds = knownSeats.Select(seat => seat.Id).ToHashSet();
        var unavailable = seatIds.Where(id => !knownIds.Contains(id)).ToList();

        var liveStatuses = await holdStore.GetLiveStatusesAsync(eventId, seatIds, cancellationToken);
        foreach (var seat in knownSeats)
        {
            var status = seat.Status == SeatStatus.Sold
                ? SeatStatus.Sold
                : liveStatuses.TryGetValue(seat.Id, out var live) ? live : seat.Status;
            if (status != SeatStatus.Available)
                unavailable.Add(seat.Id);
        }

        return new CheckAvailabilityResult(unavailable.Count == 0, unavailable);
    }

    private async Task<bool> ReleaseQuoteAsync(
        HoldQuote quote,
        CancellationToken cancellationToken)
    {
        if (quote.State is HoldQuoteState.Invalidated or HoldQuoteState.Expired)
            return true;

        if (quote.State == HoldQuoteState.Consumed)
            return false;

        var now = timeProvider.GetUtcNow();
        var releaseResult = await holdStore.ReleaseAsync(ToReleaseRequest(quote), cancellationToken);
        if (!releaseResult.IsSuccessful)
            throw new InvalidOperationException(
                $"Cached hold release conflicted with durable authority: {releaseResult.Reason ?? "unknown conflict"}.");

        if (quote.ExpiresAt <= now)
            quote.Expire(now);
        else
            quote.Invalidate(now);

        await availabilityEventPublisher.PublishAsync(
            quote.EventId,
            quote.SeatIds,
            "Available",
            quote.Generation,
            now,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<ConfirmSeatAllocationResult> CompleteConfirmSuccessAsync(
        ReservationOperationReceipt receipt,
        ConfirmSeatAllocationRequest request,
        HoldQuote quote,
        IReadOnlyList<Guid> seatIds,
        string fingerprint,
        DateTimeOffset now,
        bool publishAvailability,
        CancellationToken cancellationToken)
    {
        var snapshot = new ConfirmSeatsReceiptSnapshot(
            true,
            request.OrderId,
            quote.EventId,
            quote.Id,
            quote.OwnerUserId,
            quote.Generation,
            seatIds,
            Guid.Empty,
            quote.PriceSnapshotVersion,
            quote.Amount,
            quote.Currency,
            now);
        receipt.CompletePendingConfirm(fingerprint, snapshot, now);

        if (publishAvailability)
        {
            await availabilityEventPublisher.PublishAsync(
                quote.EventId,
                seatIds,
                "Sold",
                quote.Generation,
                now,
                cancellationToken);
        }

        // Allocation rows, quote.Consume, the exact receipt and the bus-outbox message are all
        // flushed by this same SaveChanges call/ambient EF transaction.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return MapReceipt(receipt.ReplayConfirm(fingerprint));
    }

    private async Task<ConfirmSeatAllocationResult> CompleteConfirmFailureAsync(
        ReservationOperationReceipt receipt,
        ConfirmSeatAllocationRequest request,
        string fingerprint,
        DateTimeOffset now,
        string failureCode,
        string failureReason,
        CancellationToken cancellationToken)
    {
        var snapshot = new ConfirmSeatsReceiptSnapshot(
            false,
            request.OrderId,
            request.EventId,
            request.HoldId,
            request.OwnerUserId,
            request.Generation,
            request.SeatIds.Order().ToArray(),
            Guid.Empty,
            request.PriceSnapshotVersion,
            request.Amount,
            request.Currency.ToUpperInvariant(),
            now,
            failureCode,
            failureReason);
        receipt.CompletePendingConfirm(fingerprint, snapshot, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return MapReceipt(receipt.ReplayConfirm(fingerprint));
    }

    private static bool MatchesAuthority(
        HoldQuote quote,
        ConfirmSeatAllocationRequest request,
        IReadOnlyList<Guid> authoritativeSeatIds) =>
        quote.EventId == request.EventId &&
        quote.OwnerUserId == request.OwnerUserId &&
        authoritativeSeatIds.SequenceEqual(request.SeatIds.Order()) &&
        quote.PriceSnapshotVersion == request.PriceSnapshotVersion &&
        quote.Amount == request.Amount &&
        string.Equals(quote.Currency, request.Currency, StringComparison.OrdinalIgnoreCase) &&
        quote.Generation == request.Generation;

    private static bool HasAllocationConflict(
        IReadOnlyList<SeatAllocation> allocations,
        HoldQuote quote,
        Guid orderId,
        DateTimeOffset now) =>
        allocations.Any(allocation =>
            allocation.State == SeatAllocationState.Confirmed &&
            (allocation.HoldId != quote.Id ||
             allocation.OrderId != orderId ||
             allocation.OwnerUserId != quote.OwnerUserId ||
             allocation.Generation != quote.Generation) ||
            allocation.State == SeatAllocationState.Held && allocation.ExpiresAt > now &&
            (allocation.HoldId != quote.Id ||
             allocation.OwnerUserId != quote.OwnerUserId ||
             allocation.Generation != quote.Generation) ||
            allocation.State is SeatAllocationState.Released or SeatAllocationState.Expired &&
            allocation.Generation >= quote.Generation);

    private static bool IsExactConfirmedAllocationSet(
        IReadOnlyList<SeatAllocation> allocations,
        HoldQuote quote,
        Guid orderId,
        IReadOnlyList<Guid> authoritativeSeatIds)
    {
        if (allocations.Count != authoritativeSeatIds.Count)
            return false;

        var bySeat = allocations.ToDictionary(allocation => allocation.SeatId);
        return authoritativeSeatIds.All(seatId =>
            bySeat.TryGetValue(seatId, out var allocation) &&
            allocation.EventId == quote.EventId &&
            allocation.HoldId == quote.Id &&
            allocation.OwnerUserId == quote.OwnerUserId &&
            allocation.OrderId == orderId &&
            allocation.Generation == quote.Generation &&
            allocation.State == SeatAllocationState.Confirmed);
    }

    private static ReleaseHoldStoreRequest ToReleaseRequest(HoldQuote quote) => new(
        quote.EventId,
        quote.Id,
        quote.OwnerUserId,
        quote.Generation,
        quote.SeatIds);

    private static HoldQuoteResponse MapQuote(HoldQuote quote) => new(
        quote.Id,
        quote.EventId,
        quote.OwnerUserId,
        quote.SeatIds,
        quote.Amount,
        quote.Currency,
        quote.PriceSnapshotVersion,
        quote.Generation,
        quote.ExpiresAt);

    private static ConfirmSeatAllocationResult MapReceipt(ConfirmSeatsReceiptSnapshot snapshot) => new(
        snapshot.Succeeded,
        snapshot.OrderId,
        snapshot.EventId,
        snapshot.HoldId,
        snapshot.OwnerUserId,
        snapshot.Generation,
        snapshot.SeatIds,
        snapshot.AllocationReference,
        snapshot.PriceSnapshotVersion,
        snapshot.Amount,
        snapshot.Currency,
        snapshot.DecidedAt,
        snapshot.FailureCode,
        snapshot.FailureReason);

    private ConfirmSeatAllocationResult IdempotencyConflict(
        ConfirmSeatAllocationRequest request,
        Guid receiptId) => new(
        false,
        request.OrderId,
        request.EventId,
        request.HoldId,
        request.OwnerUserId,
        request.Generation,
        request.SeatIds.Order().ToArray(),
        receiptId,
        request.PriceSnapshotVersion,
        request.Amount,
        request.Currency.ToUpperInvariant(),
        timeProvider.GetUtcNow(),
        "IdempotencyConflict",
        "The operation key was already used with a different confirm payload.");

    private static ReleaseSeatAllocationResult MapReleaseReceipt(ReleaseSeatsReceiptSnapshot snapshot) => new(
        snapshot.Succeeded,
        snapshot.OrderId,
        snapshot.EventId,
        snapshot.HoldId,
        snapshot.OwnerUserId,
        snapshot.Generation,
        snapshot.ReleasedSeatIds,
        snapshot.StaleSeatIds,
        snapshot.OperationReference,
        snapshot.DecidedAt,
        snapshot.FailureCode,
        snapshot.FailureReason);

    private ReleaseSeatAllocationResult ReleaseIdempotencyConflict(
        ReleaseSeatAllocationRequest request,
        Guid receiptId) => new(
        false,
        request.OrderId,
        request.EventId,
        request.HoldId,
        request.OwnerUserId,
        request.Generation,
        [],
        [],
        receiptId,
        timeProvider.GetUtcNow(),
        "IdempotencyConflict",
        "The operation key was already used with a different compensation payload.");

    private void ValidateHoldRequest(HoldSeatsRequest request)
    {
        if (request.EventId == Guid.Empty)
            throw new ArgumentException("Event id is required.", nameof(request));
        if (request.OwnerUserId == Guid.Empty)
            throw new ArgumentException("Hold owner is required.", nameof(request));
        if (request.SeatIds.Count == 0)
            throw new ArgumentException("At least one seat id is required.", nameof(request));
        if (request.SeatIds.Count > options.MaxSeatsPerHold)
            throw new ArgumentException(
                $"A hold cannot contain more than {options.MaxSeatsPerHold} seats.", nameof(request));
        if (request.SeatIds.Distinct().Count() != request.SeatIds.Count)
            throw new ArgumentException("Seat ids must be unique.", nameof(request));
    }

    private static void ValidateReleaseRequest(ReleaseSeatAllocationRequest request)
    {
        if (request.OrderId == Guid.Empty || request.EventId == Guid.Empty || request.HoldId == Guid.Empty)
            throw new ArgumentException("Compensation authority identifiers cannot be empty.", nameof(request));
        if (request.OwnerUserId == Guid.Empty)
            throw new ArgumentException("Compensation owner cannot be an empty id.", nameof(request));
        if (request.Generation < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Compensation generation cannot be negative.");
        if (request.SeatIds.Distinct().Count() != request.SeatIds.Count)
            throw new ArgumentException("Compensation seat ids must be unique.", nameof(request));
    }

    /// <summary>
    /// Per-user throttles (Commit 5 delta). These are availability protections, not correctness
    /// ones: without them a single authenticated customer can hold the whole venue on repeat and
    /// deny sales without ever paying. Returns null when the request is within budget.
    /// </summary>
    private async Task<HoldSeatsResult?> CheckHoldBudgetAsync(
        HoldSeatsRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var windowStart = now.AddSeconds(-options.HoldRateLimitWindowSeconds);
        var createdInWindow = await holdQuoteRepository.CountCreatedByOwnerSinceAsync(
            request.EventId,
            request.OwnerUserId,
            windowStart,
            cancellationToken);
        if (createdInWindow >= options.MaxHoldsPerUserPerWindow)
        {
            ReservationCompensationMetrics.RecordHoldRejection(HoldRejectionReason.RateLimited);
            return HoldSeatsResult.RateLimited(
                $"At most {options.MaxHoldsPerUserPerWindow} holds per " +
                $"{options.HoldRateLimitWindowSeconds}s are allowed for this event.");
        }

        var activeQuotes = await holdQuoteRepository.GetActiveByOwnerAsync(
            request.EventId,
            request.OwnerUserId,
            now,
            cancellationToken);
        if (activeQuotes.Count >= options.MaxActiveHoldsPerUserPerEvent)
        {
            ReservationCompensationMetrics.RecordHoldRejection(HoldRejectionReason.HoldCapExceeded);
            return HoldSeatsResult.CapExceeded(
                $"At most {options.MaxActiveHoldsPerUserPerEvent} active holds per event are allowed.");
        }

        var heldSeatCount = activeQuotes.Sum(quote => quote.SeatIds.Length);
        if (heldSeatCount + request.SeatIds.Count > options.MaxHeldSeatsPerUserPerEvent)
        {
            ReservationCompensationMetrics.RecordHoldRejection(HoldRejectionReason.HoldCapExceeded);
            return HoldSeatsResult.CapExceeded(
                $"At most {options.MaxHeldSeatsPerUserPerEvent} seats per event may be held at once " +
                $"(currently holding {heldSeatCount}).");
        }

        return null;
    }

    private static void ValidateConfirmRequest(ConfirmSeatAllocationRequest request)
    {
        if (request.OrderId == Guid.Empty || request.EventId == Guid.Empty ||
            request.HoldId == Guid.Empty || request.OwnerUserId == Guid.Empty)
            throw new ArgumentException("Confirm authority identifiers cannot be empty.", nameof(request));
        if (request.Generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Confirm generation must be positive.");
        if (request.PriceSnapshotVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Price snapshot version must be positive.");
        if (request.Amount < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Confirm amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
            throw new ArgumentException("Confirm currency must be a three-letter code.", nameof(request));
        if (request.SeatIds.Count == 0 || request.SeatIds.Distinct().Count() != request.SeatIds.Count)
            throw new ArgumentException("Confirm requires a non-empty exact seat set.", nameof(request));
    }
}
