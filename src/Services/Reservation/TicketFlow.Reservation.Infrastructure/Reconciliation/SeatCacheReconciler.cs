using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Allocations;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.Seats;
using TicketFlow.Reservation.Infrastructure.Redis;

namespace TicketFlow.Reservation.Infrastructure.Reconciliation;

public enum SeatCacheReconcileMode
{
    Report,
    RepairCache,
    QuarantineConflict,
}

public enum SeatCacheDriftKind
{
    MissingValidSeat,
    CacheOnlySeat,
    StateMismatch,
    TokenMismatch,
    ReverseIndexMismatch,
    AmbiguousAuthority,
}

public sealed record SeatCacheDrift(
    Guid EventId,
    Guid SeatId,
    SeatCacheDriftKind Kind,
    string Details,
    bool IsOwnershipConflict = false);

public sealed record SeatCacheReconcileResult(
    SeatCacheReconcileMode Mode,
    Guid EventId,
    long SnapshotVersion,
    bool RepairApplied,
    IReadOnlyList<SeatCacheDrift> DetectedDrifts,
    IReadOnlyList<SeatCacheDrift> RemainingDrifts,
    IReadOnlyList<Guid> QuarantinedSeatIds);

public interface ISeatCacheReconciler
{
    Task<SeatCacheReconcileResult> ReconcileAsync(
        Guid eventId,
        SeatCacheReconcileMode mode,
        CancellationToken cancellationToken = default);
}

public interface ISeatCacheBootstrapper
{
    Task<IReadOnlyList<SeatCacheReconcileResult>> ReconcileAllAsync(
        SeatCacheReconcileMode mode,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Rebuilds Redis from two explicit PostgreSQL authorities: active HoldQuote rows represent Held,
/// and confirmed SeatAllocation rows linked to consumed quotes represent Sold. Any overlapping or
/// incomplete ownership is reported and never auto-resolved by repair-cache.
/// </summary>
public sealed class SeatCacheReconciler(
    ReservationDbContext dbContext,
    ISeatHoldStore holdStore,
    IConnectionMultiplexer redis,
    TimeProvider timeProvider) : ISeatCacheReconciler
{
    private const int MaxConvergenceAttempts = 3;

    private IDatabase Cache => redis.GetDatabase();

    public async Task<SeatCacheReconcileResult> ReconcileAsync(
        Guid eventId,
        SeatCacheReconcileMode mode,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SeatCacheDrift>? initiallyDetected = null;
        IReadOnlyList<SeatCacheDrift> remaining = [];
        AuthoritySnapshot? latestSnapshot = null;
        var repairApplied = false;
        var quarantined = new HashSet<Guid>();

        for (var attempt = 0; attempt < MaxConvergenceAttempts; attempt++)
        {
            var snapshot = await LoadAuthorityAsync(eventId, mode, cancellationToken);
            latestSnapshot = snapshot;
            repairApplied |= snapshot.AuthorityUpdated;
            var detected = await InspectAsync(snapshot);
            initiallyDetected ??= detected;

            // Report is deliberately read-only: no quote expiry transition, cache cleanup,
            // rebuild, or quarantine is allowed on this path.
            if (mode == SeatCacheReconcileMode.Report)
                return Result(mode, snapshot, false, detected, detected, []);

            var ownershipConflicts = OwnershipConflictSeatIds(snapshot, detected);

            // RepairCache fails closed for the whole event rather than choosing an owner.
            if (mode == SeatCacheReconcileMode.RepairCache && ownershipConflicts.Length > 0)
            {
                return Result(
                    mode,
                    snapshot,
                    repairApplied,
                    initiallyDetected,
                    detected,
                    quarantined.Order().ToArray());
            }

            foreach (var (seatId, inactive) in snapshot.InactiveAllocations)
            {
                // A newer proven quote/allocation supersedes this terminal token. Conflicting
                // seats are contained by QuarantineConflict instead of being destructively
                // cleaned, so their evidence remains available for diagnosis.
                if (snapshot.Allocations.ContainsKey(seatId) || ownershipConflicts.Contains(seatId))
                    continue;

                var removed = await RemoveInactiveCacheAsync(snapshot.EventId, inactive);
                if (removed)
                    continue;

                var conflict = new SeatCacheDrift(
                    snapshot.EventId,
                    inactive.Allocation.SeatId,
                    SeatCacheDriftKind.TokenMismatch,
                    "A newer Redis owner replaced an expired or invalidated durable hold.",
                    IsOwnershipConflict: true);
                detected = detected.Append(conflict).ToArray();
                ownershipConflicts = OwnershipConflictSeatIds(snapshot, detected);
                if (mode == SeatCacheReconcileMode.RepairCache)
                {
                    return Result(
                        mode,
                        snapshot,
                        repairApplied,
                        initiallyDetected,
                        detected,
                        quarantined.Order().ToArray());
                }
            }

            if (detected.Count == 0)
            {
                return Result(
                    mode,
                    snapshot,
                    repairApplied,
                    initiallyDetected,
                    [],
                    quarantined.Order().ToArray());
            }

            var validSeatSet = snapshot.ValidSeatIds.ToHashSet();
            var explicitQuarantine = mode == SeatCacheReconcileMode.QuarantineConflict
                ? ownershipConflicts.Where(validSeatSet.Contains).Order().ToArray()
                : [];
            var explicitQuarantineSet = explicitQuarantine.ToHashSet();
            var rebuildAllocations = snapshot.Allocations.Values
                .Where(item => validSeatSet.Contains(item.SeatId) &&
                    !explicitQuarantineSet.Contains(item.SeatId))
                .OrderBy(item => item.SeatId)
                .ToArray();
            var rebuild = await holdStore.RebuildAsync(
                new RebuildSeatCacheRequest(
                    eventId,
                    snapshot.ValidSeatIds,
                    rebuildAllocations,
                    snapshot.SnapshotVersion,
                    explicitQuarantine),
                cancellationToken);
            repairApplied |= rebuild.Applied;
            if (rebuild.Applied)
                quarantined.UnionWith(explicitQuarantine);

            // The authority may have changed after Inspect but before the Redis Lua ran. Always
            // reload PostgreSQL and inspect against a fresh snapshot. A bounded retry removes a
            // stale hold that this attempt may have briefly reconstructed without looping forever
            // under continuous writes.
            var refreshed = await LoadAuthorityAsync(eventId, mode, cancellationToken);
            latestSnapshot = refreshed;
            repairApplied |= refreshed.AuthorityUpdated;
            remaining = await InspectAsync(refreshed);

            var actionable = mode == SeatCacheReconcileMode.QuarantineConflict
                ? remaining.Where(drift => !quarantined.Contains(drift.SeatId)).ToArray()
                : remaining;
            if (actionable.Count == 0)
            {
                return Result(
                    mode,
                    refreshed,
                    repairApplied,
                    initiallyDetected,
                    remaining,
                    quarantined.Order().ToArray());
            }
        }

        return Result(
            mode,
            latestSnapshot!,
            repairApplied,
            initiallyDetected ?? [],
            remaining,
            quarantined.Order().ToArray());
    }

    private static Guid[] OwnershipConflictSeatIds(
        AuthoritySnapshot snapshot,
        IEnumerable<SeatCacheDrift> drifts)
    {
        var conflictSeatIds = drifts
            .Where(drift => drift.IsOwnershipConflict ||
                drift.Kind == SeatCacheDriftKind.AmbiguousAuthority)
            .Select(drift => drift.SeatId)
            .Distinct()
            .ToHashSet();
        var conflictHoldIds = snapshot.Allocations.Values
            .Where(allocation => conflictSeatIds.Contains(allocation.SeatId))
            .Select(allocation => allocation.HoldId)
            .ToHashSet();

        // One hold/confirmation owns an exact seat set. If any member is ambiguous, containing
        // only that seat would leave reverse-index drift on its siblings and misrepresent the
        // authority as a partial allocation. Contain the complete durable ownership unit.
        conflictSeatIds.UnionWith(snapshot.Allocations.Values
            .Where(allocation => conflictHoldIds.Contains(allocation.HoldId))
            .Select(allocation => allocation.SeatId));
        return conflictSeatIds.Order().ToArray();
    }

    private async Task<AuthoritySnapshot> LoadAuthorityAsync(
        Guid eventId,
        SeatCacheReconcileMode mode,
        CancellationToken cancellationToken)
    {
        var seats = await dbContext.Seats.AsNoTracking()
            .Where(seat => seat.EventId == eventId)
            .Select(seat => new { seat.Id, seat.Status })
            .OrderBy(seat => seat.Id)
            .ToListAsync(cancellationToken);
        var rows = await dbContext.SeatAllocations.AsNoTracking()
            .Where(allocation => allocation.EventId == eventId)
            .ToListAsync(cancellationToken);

        var quotesQuery = dbContext.HoldQuotes.Where(quote => quote.EventId == eventId);
        var quotes = await quotesQuery.AsNoTracking().ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var authorityUpdated = false;
        var intrinsicDrifts = new List<SeatCacheDrift>();
        if (mode == SeatCacheReconcileMode.Report)
        {
            foreach (var quote in quotes.Where(quote =>
                         quote.State == HoldQuoteState.Active && quote.ExpiresAt <= now))
            {
                foreach (var seatId in quote.SeatIds)
                {
                    intrinsicDrifts.Add(new SeatCacheDrift(
                        eventId,
                        seatId,
                        SeatCacheDriftKind.StateMismatch,
                        $"Hold quote '{quote.Id}' is past expiry but still durable Active."));
                }
            }
        }
        else
        {
            // Conditional server-side transition avoids overwriting a concurrent release/confirm
            // from a stale tracked entity. Both mutating modes repair this safe lifecycle drift.
            var expiredCount = await quotesQuery
                .Where(quote => quote.State == HoldQuoteState.Active && quote.ExpiresAt <= now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(quote => quote.State, HoldQuoteState.Expired)
                        .SetProperty(quote => quote.OrderId, (Guid?)null)
                        .SetProperty(quote => quote.TerminalAt, (DateTimeOffset?)now),
                    cancellationToken);
            authorityUpdated = expiredCount > 0;
            // Reload even when no row expired: a release/confirm can have terminalized a quote
            // after the first read but before the conditional update.
            quotes = await quotesQuery.AsNoTracking().ToListAsync(cancellationToken);
        }

        var validSeatIds = seats.Select(seat => seat.Id).ToArray();
        var validSeatSet = validSeatIds.ToHashSet();
        var seatStatus = seats.ToDictionary(seat => seat.Id, seat => seat.Status);
        var quoteById = quotes.ToDictionary(quote => quote.Id);
        var canonical = new Dictionary<Guid, CachedSeatAllocation>();
        var inactive = new Dictionary<Guid, InactiveAllocation>();
        var ambiguities = new Dictionary<Guid, string>();

        void MarkAmbiguous(Guid seatId, string details, CachedSeatAllocation? candidate = null)
        {
            ambiguities[seatId] = ambiguities.TryGetValue(seatId, out var existing)
                ? $"{existing} {details}"
                : details;

            if (canonical.TryGetValue(seatId, out var current))
                canonical[seatId] = current with { State = CachedAllocationState.Quarantined };
            else if (candidate is not null)
                canonical[seatId] = candidate with { State = CachedAllocationState.Quarantined };
        }

        void AddCanonical(CachedSeatAllocation candidate, string conflictDetails)
        {
            if (!validSeatSet.Contains(candidate.SeatId))
            {
                MarkAmbiguous(candidate.SeatId, "Authority references a seat absent from PostgreSQL inventory.", candidate);
                return;
            }

            if (!canonical.TryGetValue(candidate.SeatId, out var current))
            {
                canonical[candidate.SeatId] = candidate;
                return;
            }

            if (current == candidate)
                return;

            MarkAmbiguous(candidate.SeatId, conflictDetails, candidate);
        }

        foreach (var row in rows)
        {
            if (row.State == SeatAllocationState.Confirmed)
            {
                var valid = row.OwnerUserId.HasValue && row.OrderId.HasValue && row.Generation > 0 &&
                    quoteById.TryGetValue(row.HoldId, out var consumedQuote) &&
                    consumedQuote.State == HoldQuoteState.Consumed &&
                    consumedQuote.OrderId == row.OrderId &&
                    consumedQuote.OwnerUserId == row.OwnerUserId &&
                    consumedQuote.Generation == row.Generation;
                var candidate = new CachedSeatAllocation(
                    row.SeatId,
                    row.HoldId,
                    row.OwnerUserId ?? Guid.Empty,
                    row.Generation,
                    valid ? CachedAllocationState.Confirmed : CachedAllocationState.Quarantined,
                    row.OrderId,
                    null);
                AddCanonical(candidate, "A confirmed allocation overlaps another durable owner.");
                if (!valid)
                {
                    MarkAmbiguous(
                        row.SeatId,
                        $"Confirmed allocation '{row.Id}' is not linked to a matching consumed quote.",
                        candidate);
                }
                continue;
            }

            if (row.State == SeatAllocationState.Held)
            {
                var candidate = new CachedSeatAllocation(
                    row.SeatId,
                    row.HoldId,
                    row.OwnerUserId ?? Guid.Empty,
                    row.Generation,
                    CachedAllocationState.Quarantined,
                    null,
                    row.ExpiresAt);
                MarkAmbiguous(
                    row.SeatId,
                    $"Legacy Held allocation '{row.Id}' coexists with the quote-only hold lifecycle.",
                    candidate);
                continue;
            }

            if (row.OwnerUserId.HasValue)
            {
                var previous = new CachedSeatAllocation(
                    row.SeatId,
                    row.HoldId,
                    row.OwnerUserId.Value,
                    row.Generation,
                    row.OrderId.HasValue ? CachedAllocationState.Confirmed : CachedAllocationState.Held,
                    row.OrderId,
                    row.ExpiresAt);
                inactive[row.SeatId] = new InactiveAllocation(
                    previous,
                    HeldToken(previous),
                    row.OrderId.HasValue ? ConfirmedToken(previous) : string.Empty,
                    row.HoldId.ToString());
            }
        }

        foreach (var quote in quotes)
        {
            if (quote.State == HoldQuoteState.Active && quote.ExpiresAt > now)
            {
                foreach (var seatId in quote.SeatIds)
                {
                    var candidate = new CachedSeatAllocation(
                        seatId,
                        quote.Id,
                        quote.OwnerUserId,
                        quote.Generation,
                        CachedAllocationState.Held,
                        null,
                        quote.ExpiresAt);
                    AddCanonical(candidate, "An active quote overlaps another active or confirmed authority.");
                    if (seatStatus.TryGetValue(seatId, out var status) && status == SeatStatus.Sold)
                    {
                        MarkAmbiguous(
                            seatId,
                            $"Active quote '{quote.Id}' overlaps a durably Sold inventory seat.",
                            candidate);
                    }
                }

                continue;
            }

            if (quote.State is HoldQuoteState.Expired or HoldQuoteState.Invalidated ||
                quote.State == HoldQuoteState.Active && quote.ExpiresAt <= now)
            {
                foreach (var seatId in quote.SeatIds)
                {
                    var previous = new CachedSeatAllocation(
                        seatId,
                        quote.Id,
                        quote.OwnerUserId,
                        quote.Generation,
                        CachedAllocationState.Held,
                        null,
                        quote.ExpiresAt);
                    if (!inactive.TryGetValue(seatId, out var existing) ||
                        existing.Allocation.Generation < quote.Generation)
                    {
                        inactive[seatId] = new InactiveAllocation(
                            previous,
                            HeldToken(previous),
                            string.Empty,
                            quote.Id.ToString());
                    }
                }
            }
        }

        foreach (var quote in quotes.Where(quote => quote.State == HoldQuoteState.Consumed))
        {
            var confirmed = rows
                .Where(row => row.HoldId == quote.Id && row.State == SeatAllocationState.Confirmed)
                .OrderBy(row => row.SeatId)
                .ToArray();
            var exact = confirmed.Length == quote.SeatIds.Length &&
                confirmed.Select(row => row.SeatId).SequenceEqual(quote.SeatIds.Order()) &&
                confirmed.All(row =>
                    row.EventId == quote.EventId &&
                    row.OrderId == quote.OrderId &&
                    row.OwnerUserId == quote.OwnerUserId &&
                    row.Generation == quote.Generation);
            if (exact)
                continue;

            foreach (var seatId in quote.SeatIds.Union(confirmed.Select(row => row.SeatId)))
            {
                MarkAmbiguous(
                    seatId,
                    $"Consumed quote '{quote.Id}' does not have one exact matching confirmed allocation set.",
                    canonical.GetValueOrDefault(seatId));
            }
        }

        foreach (var seat in seats)
        {
            var confirmed = canonical.TryGetValue(seat.Id, out var allocation) &&
                allocation.State == CachedAllocationState.Confirmed;
            if (seat.Status == SeatStatus.Sold && !confirmed)
            {
                MarkAmbiguous(
                    seat.Id,
                    "Inventory is durably Sold but no exact confirmed allocation/consumed quote proves ownership.",
                    allocation);
            }
            else if (seat.Status != SeatStatus.Sold && confirmed)
            {
                MarkAmbiguous(
                    seat.Id,
                    "A confirmed allocation exists while durable inventory is not Sold.",
                    allocation);
            }
        }

        var updatedVersion = rows.Select(row => row.UpdatedAt.ToUnixTimeMilliseconds())
            .Concat(quotes.Select(quote =>
                (quote.TerminalAt ?? quote.ExpiresAt).ToUnixTimeMilliseconds()))
            .DefaultIfEmpty(0L)
            .Max();
        var snapshotVersion = Math.Max(now.ToUnixTimeMilliseconds(), updatedVersion);
        return new AuthoritySnapshot(
            eventId,
            validSeatIds,
            canonical,
            inactive,
            ambiguities,
            intrinsicDrifts,
            snapshotVersion,
            authorityUpdated);
    }

    private async Task<IReadOnlyList<SeatCacheDrift>> InspectAsync(AuthoritySnapshot snapshot)
    {
        var drifts = new List<SeatCacheDrift>(snapshot.IntrinsicDrifts);
        var cachedValid = ParseGuidSet(await Cache.SetMembersAsync(RedisKeys.SeatsSet(snapshot.EventId)));
        var sold = ParseGuidSet(await Cache.SetMembersAsync(RedisKeys.SoldSet(snapshot.EventId)));
        var quarantined = ParseGuidSet(await Cache.SetMembersAsync(RedisKeys.QuarantinedSet(snapshot.EventId)));
        var authoritativeSeats = snapshot.ValidSeatIds.ToHashSet();

        // Durable ownership that points outside the inventory is still an ambiguity. Do not
        // limit this check to valid seats: otherwise a missing Seat row makes the corrupt quote
        // or allocation invisible and startup repair can incorrectly report a clean cache.
        foreach (var (seatId, ambiguity) in snapshot.Ambiguities
                     .Where(item => !authoritativeSeats.Contains(item.Key)))
        {
            drifts.Add(new SeatCacheDrift(
                snapshot.EventId,
                seatId,
                SeatCacheDriftKind.AmbiguousAuthority,
                ambiguity,
                IsOwnershipConflict: true));
        }

        foreach (var seatId in authoritativeSeats.Except(cachedValid))
        {
            drifts.Add(new SeatCacheDrift(
                snapshot.EventId,
                seatId,
                SeatCacheDriftKind.MissingValidSeat,
                "Seat exists in PostgreSQL inventory but not in the Redis valid-seat set."));
        }

        var cacheOnly = cachedValid.Union(sold).Union(quarantined).Except(authoritativeSeats).ToArray();
        foreach (var seatId in cacheOnly)
        {
            var orphanHold = await Cache.StringGetAsync(RedisKeys.Hold(snapshot.EventId, seatId));
            var orphanAllocation = await Cache.StringGetAsync(RedisKeys.Allocation(snapshot.EventId, seatId));
            drifts.Add(new SeatCacheDrift(
                snapshot.EventId,
                seatId,
                SeatCacheDriftKind.CacheOnlySeat,
                "Redis references a seat that is absent from PostgreSQL inventory.",
                sold.Contains(seatId) || quarantined.Contains(seatId) ||
                orphanHold.HasValue || orphanAllocation.HasValue));
        }

        var orderedSeats = authoritativeSeats.Order().ToArray();
        var holdValues = orderedSeats.Length == 0
            ? []
            : await Cache.StringGetAsync(orderedSeats
                .Select(seatId => (RedisKey)RedisKeys.Hold(snapshot.EventId, seatId))
                .ToArray());
        var allocationValues = orderedSeats.Length == 0
            ? []
            : await Cache.StringGetAsync(orderedSeats
                .Select(seatId => (RedisKey)RedisKeys.Allocation(snapshot.EventId, seatId))
                .ToArray());
        var holds = orderedSeats.Select((seatId, index) => (seatId, value: holdValues[index].ToString()))
            .ToDictionary(item => item.seatId, item => item.value);
        var allocationTokens = orderedSeats
            .Select((seatId, index) => (seatId, value: allocationValues[index].ToString()))
            .ToDictionary(item => item.seatId, item => item.value);

        foreach (var seatId in orderedSeats)
        {
            var hold = holds[seatId];
            var allocationToken = allocationTokens[seatId];
            snapshot.Allocations.TryGetValue(seatId, out var expected);

            if (snapshot.Ambiguities.TryGetValue(seatId, out var ambiguity))
            {
                drifts.Add(new SeatCacheDrift(
                    snapshot.EventId,
                    seatId,
                    SeatCacheDriftKind.AmbiguousAuthority,
                    ambiguity,
                    IsOwnershipConflict: true));
                continue;
            }

            if (expected is null)
            {
                if (sold.Contains(seatId) || quarantined.Contains(seatId) ||
                    hold.Length > 0 || allocationToken.Length > 0)
                {
                    var safelyInactive = snapshot.InactiveAllocations.TryGetValue(seatId, out var inactive) &&
                        (hold.Length == 0 || hold == inactive.HeldToken || hold == inactive.LegacyHeldToken) &&
                        (allocationToken.Length == 0 || allocationToken == inactive.ConfirmedToken);
                    drifts.Add(new SeatCacheDrift(
                        snapshot.EventId,
                        seatId,
                        SeatCacheDriftKind.StateMismatch,
                        "PostgreSQL says Available but Redis has an active allocation.",
                        !safelyInactive && (hold.Length > 0 || allocationToken.Length > 0)));
                }
                continue;
            }

            if (expected.State == CachedAllocationState.Quarantined)
            {
                if (!quarantined.Contains(seatId) || !sold.Contains(seatId))
                {
                    drifts.Add(new SeatCacheDrift(
                        snapshot.EventId,
                        seatId,
                        SeatCacheDriftKind.StateMismatch,
                        "An ambiguous durable allocation is not fail-closed in Redis.",
                        true));
                }
                continue;
            }

            var heldToken = HeldToken(expected);
            var confirmedToken = ConfirmedToken(expected);
            if (expected.State == CachedAllocationState.Held)
            {
                var hasDifferentHeldOwner = hold.Length > 0 && hold != heldToken;
                var hasConfirmedClaim = allocationToken.Length > 0;
                if (hold != heldToken || allocationToken.Length > 0 ||
                    sold.Contains(seatId) || quarantined.Contains(seatId))
                {
                    drifts.Add(new SeatCacheDrift(
                        snapshot.EventId,
                        seatId,
                        SeatCacheDriftKind.TokenMismatch,
                        "Redis held token/state does not match the active quote owner/generation.",
                        hasDifferentHeldOwner || hasConfirmedClaim));
                }
            }
            else if (allocationToken != confirmedToken || !sold.Contains(seatId) ||
                     quarantined.Contains(seatId) || hold.Length > 0)
            {
                var hasDifferentConfirmedOwner =
                    allocationToken.Length > 0 && allocationToken != confirmedToken;
                var hasHeldClaim = hold.Length > 0;
                drifts.Add(new SeatCacheDrift(
                    snapshot.EventId,
                    seatId,
                    SeatCacheDriftKind.TokenMismatch,
                    "Redis confirmed token/state does not match the confirmed allocation.",
                    hasDifferentConfirmedOwner || hasHeldClaim));
            }
        }

        foreach (var holdGroup in snapshot.Allocations.Values
                     .Where(item => item.State == CachedAllocationState.Held)
                     .GroupBy(item => item.HoldId))
        {
            var expectedSeats = holdGroup.Select(item => item.SeatId).Order().ToArray();
            var cachedSeats = ParseGuidSet(await Cache.SetMembersAsync(RedisKeys.HoldSeatsSet(holdGroup.Key)))
                .Order()
                .ToArray();
            if (!cachedSeats.SequenceEqual(expectedSeats))
            {
                var unexpectedSeats = cachedSeats.Except(expectedSeats).ToArray();
                var hasConflictingUnexpectedOwner = unexpectedSeats.Any(seatId =>
                    !holds.TryGetValue(seatId, out var token) || token.Length > 0 ||
                    allocationTokens.TryGetValue(seatId, out var allocationToken) &&
                    allocationToken.Length > 0);
                foreach (var seatId in expectedSeats)
                {
                    drifts.Add(new SeatCacheDrift(
                        snapshot.EventId,
                        seatId,
                        SeatCacheDriftKind.ReverseIndexMismatch,
                        "Redis held reverse index does not contain the exact active quote seat set.",
                        hasConflictingUnexpectedOwner));
                }
            }
        }

        foreach (var confirmation in snapshot.Allocations.Values
                     .Where(item => item.State == CachedAllocationState.Confirmed)
                     .GroupBy(item => item.HoldId))
        {
            var seats = confirmation.Select(item => item.SeatId).Order().ToArray();
            var cachedSeats = (await Cache.SetMembersAsync(
                    RedisKeys.ConfirmedSeatsSet(snapshot.EventId, confirmation.Key)))
                .Select(value => value.ToString())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var expectedSeats = seats.Select(id => id.ToString())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var first = confirmation.First();
            var expectedMeta = $"{ConfirmedToken(first)}|{string.Join(',', expectedSeats)}";
            var actualMeta = (await Cache.StringGetAsync(
                RedisKeys.ConfirmedMeta(snapshot.EventId, confirmation.Key))).ToString();
            if (!cachedSeats.SequenceEqual(expectedSeats) || actualMeta != expectedMeta)
            {
                foreach (var seatId in seats)
                {
                    drifts.Add(new SeatCacheDrift(
                        snapshot.EventId,
                        seatId,
                        SeatCacheDriftKind.ReverseIndexMismatch,
                        "Redis confirmed marker does not contain the exact allocation seat set.",
                        actualMeta.Length > 0 && actualMeta != expectedMeta));
                }
            }
        }

        return drifts
            .DistinctBy(drift => new { drift.SeatId, drift.Kind, drift.Details })
            .OrderBy(drift => drift.SeatId)
            .ThenBy(drift => drift.Kind)
            .ToArray();
    }

    private static HashSet<Guid> ParseGuidSet(IEnumerable<RedisValue> values) =>
        values.Where(value => Guid.TryParse(value.ToString(), out _))
            .Select(value => Guid.Parse(value.ToString()))
            .ToHashSet();

    private static SeatCacheReconcileResult Result(
        SeatCacheReconcileMode mode,
        AuthoritySnapshot snapshot,
        bool applied,
        IReadOnlyList<SeatCacheDrift> detected,
        IReadOnlyList<SeatCacheDrift> remaining,
        IReadOnlyList<Guid> quarantined) =>
        new(mode, snapshot.EventId, snapshot.SnapshotVersion, applied, detected, remaining, quarantined);

    private static string HeldToken(CachedSeatAllocation allocation) =>
        $"H|{allocation.HoldId}|{allocation.OwnerUserId}|{allocation.Generation}";

    private static string ConfirmedToken(CachedSeatAllocation allocation) =>
        $"S|{allocation.HoldId}|{allocation.OrderId}|{allocation.OwnerUserId}|{allocation.Generation}";

    private async Task<bool> RemoveInactiveCacheAsync(Guid eventId, InactiveAllocation inactive)
    {
        var allocation = inactive.Allocation;
        RedisKey[] keys =
        [
            RedisKeys.Hold(eventId, allocation.SeatId),
            RedisKeys.Allocation(eventId, allocation.SeatId),
            RedisKeys.SoldSet(eventId),
            RedisKeys.QuarantinedSet(eventId),
            RedisKeys.HoldSeatsSet(allocation.HoldId),
            RedisKeys.ConfirmedSeatsSet(eventId, allocation.HoldId),
            RedisKeys.ConfirmedMeta(eventId, allocation.HoldId),
        ];
        RedisValue[] values =
        [
            allocation.SeatId.ToString(),
            inactive.HeldToken,
            inactive.ConfirmedToken,
            inactive.LegacyHeldToken,
        ];
        var result = (RedisValue[]?)await Cache.ScriptEvaluateAsync(
            ExpireCachedAllocationScript.Source,
            keys,
            values) ?? [];
        return result.Length > 0 && result[0].ToString() == "APPLIED";
    }

    private sealed record AuthoritySnapshot(
        Guid EventId,
        IReadOnlyList<Guid> ValidSeatIds,
        IReadOnlyDictionary<Guid, CachedSeatAllocation> Allocations,
        IReadOnlyDictionary<Guid, InactiveAllocation> InactiveAllocations,
        IReadOnlyDictionary<Guid, string> Ambiguities,
        IReadOnlyList<SeatCacheDrift> IntrinsicDrifts,
        long SnapshotVersion,
        bool AuthorityUpdated);

    private sealed record InactiveAllocation(
        CachedSeatAllocation Allocation,
        string HeldToken,
        string ConfirmedToken,
        string LegacyHeldToken);
}

public sealed class SeatCacheBootstrapper(
    ReservationDbContext dbContext,
    ISeatCacheReconciler reconciler) : ISeatCacheBootstrapper
{
    public async Task<IReadOnlyList<SeatCacheReconcileResult>> ReconcileAllAsync(
        SeatCacheReconcileMode mode,
        CancellationToken cancellationToken = default)
    {
        var seatEvents = dbContext.Seats.AsNoTracking().Select(seat => seat.EventId);
        var allocationEvents = dbContext.SeatAllocations.AsNoTracking().Select(allocation => allocation.EventId);
        var quoteEvents = dbContext.HoldQuotes.AsNoTracking().Select(quote => quote.EventId);
        var eventIds = await seatEvents
            .Union(allocationEvents)
            .Union(quoteEvents)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
        var results = new List<SeatCacheReconcileResult>(eventIds.Count);
        foreach (var eventId in eventIds)
            results.Add(await reconciler.ReconcileAsync(eventId, mode, cancellationToken));
        return results;
    }
}
