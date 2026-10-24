using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using TicketFlow.Phase12.HardeningTests.Infrastructure;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Allocations;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.Seats;
using TicketFlow.Reservation.Infrastructure;
using TicketFlow.Reservation.Infrastructure.Reconciliation;
using TicketFlow.Reservation.Infrastructure.Redis;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class SeatCacheReconcilerIntegrationTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Report_is_read_only_and_repair_rebuilds_active_quotes_confirmed_allocations_and_exact_reverse_indexes()
    {
        var databaseName = $"phase12_reconcile_{Guid.NewGuid():N}";
        var connectionString = DatabaseConnection(databaseName);
        await CreateDatabaseAsync(databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var heldSeat = Seat.Create(eventId, sectionId, "HELD-1");
            var soldSeat = Seat.Create(eventId, sectionId, "SOLD-1");
            var releasedSeat = Seat.Create(eventId, sectionId, "RELEASED-1");
            var availableSeat = Seat.Create(eventId, sectionId, "AVAILABLE-1");
            soldSeat.MarkSold();
            var heldQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [heldSeat.Id], 100_000m, "VND", 11, 21,
                now, now.AddMinutes(10));
            var consumedQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [soldSeat.Id], 150_000m, "VND", 11, 22,
                now, now.AddMinutes(10));
            consumedQuote.Consume(orderId, now.AddSeconds(1));
            var allocation = SeatAllocation.CreateConfirmed(
                soldSeat.Id,
                eventId,
                consumedQuote.Id,
                orderId,
                ownerId,
                consumedQuote.Generation,
                consumedQuote.PriceSnapshotVersion,
                consumedQuote.Amount,
                consumedQuote.Currency,
                now.AddSeconds(1));
            var cancelledOrderId = Guid.NewGuid();
            var cancelledQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [releasedSeat.Id], 175_000m, "VND", 11, 23,
                now, now.AddMinutes(10));
            cancelledQuote.Consume(cancelledOrderId, now.AddSeconds(1));
            var releasedAllocation = SeatAllocation.CreateConfirmed(
                releasedSeat.Id,
                eventId,
                cancelledQuote.Id,
                cancelledOrderId,
                ownerId,
                cancelledQuote.Generation,
                cancelledQuote.PriceSnapshotVersion,
                cancelledQuote.Amount,
                cancelledQuote.Currency,
                now.AddSeconds(1));
            Assert.Equal(
                SeatAllocationReleaseOutcome.Released,
                releasedAllocation.ReleaseOwned(
                    cancelledQuote.Id,
                    cancelledOrderId,
                    ownerId,
                    cancelledQuote.Generation,
                    now.AddSeconds(2)));

            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            context.Seats.AddRange(heldSeat, soldSeat, releasedSeat, availableSeat);
            context.HoldQuotes.AddRange(heldQuote, consumedQuote, cancelledQuote);
            context.SeatAllocations.AddRange(allocation, releasedAllocation);
            await context.SaveChangesAsync();

            var store = new RedisSeatHoldStore(redis);
            var reconciler = new SeatCacheReconciler(context, store, redis, TimeProvider.System);
            var report = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.Report);

            Assert.False(report.RepairApplied);
            Assert.NotEmpty(report.DetectedDrifts);
            var beforeRepair = await store.GetLiveStatusesAsync(
                eventId,
                [heldSeat.Id, soldSeat.Id, releasedSeat.Id, availableSeat.Id]);
            Assert.Equal(SeatStatus.Available, beforeRepair[heldSeat.Id]);

            var repaired = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.RepairCache);
            Assert.True(repaired.RepairApplied);
            Assert.Empty(repaired.RemainingDrifts);
            var statuses = await store.GetLiveStatusesAsync(
                eventId,
                [heldSeat.Id, soldSeat.Id, releasedSeat.Id, availableSeat.Id]);
            Assert.Equal(SeatStatus.Held, statuses[heldSeat.Id]);
            Assert.Equal(SeatStatus.Sold, statuses[soldSeat.Id]);
            Assert.Equal(SeatStatus.Available, statuses[releasedSeat.Id]);
            Assert.Equal(SeatStatus.Available, statuses[availableSeat.Id]);

            // A reverse-index-only orphan has no owner token and is therefore repairable. The
            // rebuild must recreate the exact set instead of carrying this extra member forward.
            var cache = redis.GetDatabase();
            await cache.SetAddAsync($"holdseats:{heldQuote.Id}", availableSeat.Id.ToString());
            var reverseReport = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.Report);
            Assert.Contains(
                reverseReport.DetectedDrifts,
                drift => drift.Kind == SeatCacheDriftKind.ReverseIndexMismatch && !drift.IsOwnershipConflict);

            var reverseRepair = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.RepairCache);
            Assert.True(reverseRepair.RepairApplied);
            Assert.Empty(reverseRepair.RemainingDrifts);
            var exactReverse = (await cache.SetMembersAsync($"holdseats:{heldQuote.Id}"))
                .Select(value => Guid.Parse(value.ToString()))
                .ToArray();
            Assert.Equal([heldSeat.Id], exactReverse);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Repair_refuses_an_ambiguous_owner_while_explicit_quarantine_keeps_the_seat_fail_closed()
    {
        var databaseName = $"phase12_quarantine_{Guid.NewGuid():N}";
        var connectionString = DatabaseConnection(databaseName);
        await CreateDatabaseAsync(databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var seat = Seat.Create(eventId, sectionId, "CONFLICT-1");
            var quote = HoldQuote.Create(
                Guid.NewGuid(), eventId, Guid.NewGuid(), [seat.Id], 100_000m, "VND", 7, 31,
                now, now.AddMinutes(10));

            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            context.Seats.Add(seat);
            context.HoldQuotes.Add(quote);
            await context.SaveChangesAsync();

            var store = new RedisSeatHoldStore(redis);
            await store.LoadInventoryAsync(eventId, [seat.Id]);
            var conflictingHold = await store.TryHoldAsync(new HoldStoreRequest(
                eventId,
                [seat.Id],
                Guid.NewGuid(),
                Guid.NewGuid(),
                99,
                TimeSpan.FromMinutes(10)));
            Assert.Equal(HoldStoreOutcome.Held, conflictingHold.Outcome);

            var reconciler = new SeatCacheReconciler(context, store, redis, TimeProvider.System);
            var report = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.Report);
            Assert.Contains(report.DetectedDrifts, drift => drift.IsOwnershipConflict);

            var repair = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.RepairCache);
            Assert.False(repair.RepairApplied);
            Assert.NotEmpty(repair.RemainingDrifts);

            var quarantine = await reconciler.ReconcileAsync(
                eventId,
                SeatCacheReconcileMode.QuarantineConflict);
            Assert.True(quarantine.RepairApplied);
            Assert.Equal([seat.Id], quarantine.QuarantinedSeatIds);
            var status = await store.GetLiveStatusesAsync(eventId, [seat.Id]);
            Assert.Equal(SeatStatus.Sold, status[seat.Id]);

            // Quarantine is an explicit containment mode and is repeatable after restart; it
            // preserves the unresolved evidence while keeping availability fail-closed.
            var repeated = await reconciler.ReconcileAsync(
                eventId,
                SeatCacheReconcileMode.QuarantineConflict);
            Assert.Equal([seat.Id], repeated.QuarantinedSeatIds);
            Assert.NotEmpty(repeated.RemainingDrifts);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Startup_repair_refuses_durable_authority_that_references_missing_inventory_seats()
    {
        var databaseName = $"phase12_missing_inventory_{Guid.NewGuid():N}";
        var connectionString = DatabaseConnection(databaseName);
        await CreateDatabaseAsync(databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var missingHeldSeatId = Guid.NewGuid();
            var missingConfirmedSeatId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var activeQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [missingHeldSeatId], 100_000m, "VND", 3, 41,
                now, now.AddMinutes(10));
            var consumedQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [missingConfirmedSeatId], 120_000m, "VND", 3, 42,
                now, now.AddMinutes(10));
            consumedQuote.Consume(orderId, now.AddSeconds(1));
            var allocation = SeatAllocation.CreateConfirmed(
                missingConfirmedSeatId,
                eventId,
                consumedQuote.Id,
                orderId,
                ownerId,
                consumedQuote.Generation,
                consumedQuote.PriceSnapshotVersion,
                consumedQuote.Amount,
                consumedQuote.Currency,
                now.AddSeconds(1));

            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            context.HoldQuotes.AddRange(activeQuote, consumedQuote);
            context.SeatAllocations.Add(allocation);
            await context.SaveChangesAsync();

            var store = new RedisSeatHoldStore(redis);
            var reconciler = new SeatCacheReconciler(context, store, redis, TimeProvider.System);
            var report = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.Report);

            AssertMissingInventoryAmbiguity(report, missingHeldSeatId);
            AssertMissingInventoryAmbiguity(report, missingConfirmedSeatId);

            // This is the same bootstrap path used before accepting traffic. An event with no
            // Seat rows must still be discovered through its durable quote/allocation records.
            var bootstrap = new SeatCacheBootstrapper(context, reconciler);
            var startupResults = await bootstrap.ReconcileAllAsync(SeatCacheReconcileMode.RepairCache);
            var startupRepair = Assert.Single(startupResults, result => result.EventId == eventId);

            Assert.False(startupRepair.RepairApplied);
            AssertMissingInventoryAmbiguity(startupRepair, missingHeldSeatId, remaining: true);
            AssertMissingInventoryAmbiguity(startupRepair, missingConfirmedSeatId, remaining: true);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Cross_state_tokens_are_ownership_conflicts_and_explicit_mode_quarantines_them()
    {
        var databaseName = $"phase12_cross_state_{Guid.NewGuid():N}";
        var connectionString = DatabaseConnection(databaseName);
        await CreateDatabaseAsync(databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var heldSeat = Seat.Create(eventId, sectionId, "CROSS-HELD");
            var confirmedSeat = Seat.Create(eventId, sectionId, "CROSS-CONFIRMED");
            confirmedSeat.MarkSold();
            var activeQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [heldSeat.Id], 100_000m, "VND", 5, 51,
                now, now.AddMinutes(10));
            var consumedQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [confirmedSeat.Id], 120_000m, "VND", 5, 52,
                now, now.AddMinutes(10));
            consumedQuote.Consume(orderId, now.AddSeconds(1));
            var allocation = SeatAllocation.CreateConfirmed(
                confirmedSeat.Id,
                eventId,
                consumedQuote.Id,
                orderId,
                ownerId,
                consumedQuote.Generation,
                consumedQuote.PriceSnapshotVersion,
                consumedQuote.Amount,
                consumedQuote.Currency,
                now.AddSeconds(1));

            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            context.Seats.AddRange(heldSeat, confirmedSeat);
            context.HoldQuotes.AddRange(activeQuote, consumedQuote);
            context.SeatAllocations.Add(allocation);
            await context.SaveChangesAsync();

            var store = new RedisSeatHoldStore(redis);
            await store.LoadInventoryAsync(eventId, [heldSeat.Id, confirmedSeat.Id]);
            var cache = redis.GetDatabase();

            // Durable Held, but Redis carries a confirmed-allocation token in the opposite key.
            var crossSoldToken =
                $"S|{activeQuote.Id}|{Guid.NewGuid()}|{ownerId}|{activeQuote.Generation}";
            await cache.StringSetAsync($"allocation:{eventId}:{heldSeat.Id}", crossSoldToken);
            await cache.SetAddAsync($"sold:{eventId}", heldSeat.Id.ToString());

            // Durable Confirmed, but Redis still carries a Held token in the opposite key.
            var crossHeldToken =
                $"H|{consumedQuote.Id}|{ownerId}|{consumedQuote.Generation}";
            await cache.StringSetAsync($"hold:{eventId}:{confirmedSeat.Id}", crossHeldToken);
            await cache.SetAddAsync($"holdseats:{consumedQuote.Id}", confirmedSeat.Id.ToString());
            await cache.SetAddAsync($"sold:{eventId}", confirmedSeat.Id.ToString());

            var reconciler = new SeatCacheReconciler(context, store, redis, TimeProvider.System);
            var report = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.Report);

            AssertOwnershipTokenMismatch(report, heldSeat.Id);
            AssertOwnershipTokenMismatch(report, confirmedSeat.Id);

            var repair = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.RepairCache);
            Assert.False(repair.RepairApplied);
            AssertOwnershipTokenMismatch(repair, heldSeat.Id, remaining: true);
            AssertOwnershipTokenMismatch(repair, confirmedSeat.Id, remaining: true);

            var quarantine = await reconciler.ReconcileAsync(
                eventId,
                SeatCacheReconcileMode.QuarantineConflict);
            Assert.True(quarantine.RepairApplied);
            Assert.Equal(
                new[] { heldSeat.Id, confirmedSeat.Id }.Order(),
                quarantine.QuarantinedSeatIds.Order());

            var statuses = await store.GetLiveStatusesAsync(eventId, [heldSeat.Id, confirmedSeat.Id]);
            Assert.Equal(SeatStatus.Sold, statuses[heldSeat.Id]);
            Assert.Equal(SeatStatus.Sold, statuses[confirmedSeat.Id]);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Report_does_not_expire_durable_quotes_or_mutate_conflicting_cache_evidence()
    {
        var databaseName = $"phase12_report_read_only_{Guid.NewGuid():N}";
        var connectionString = DatabaseConnection(databaseName);
        await CreateDatabaseAsync(databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var seat = Seat.Create(eventId, sectionId, "REPORT-ONLY");
            var expiredActiveQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [seat.Id], 100_000m, "VND", 9, 61,
                now.AddMinutes(-20), now.AddMinutes(-10));

            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            context.Seats.Add(seat);
            context.HoldQuotes.Add(expiredActiveQuote);
            await context.SaveChangesAsync();

            var store = new RedisSeatHoldStore(redis);
            await store.LoadInventoryAsync(eventId, [seat.Id]);
            var cache = redis.GetDatabase();
            var conflictingToken = $"H|{Guid.NewGuid()}|{Guid.NewGuid()}|999";
            var holdKey = $"hold:{eventId}:{seat.Id}";
            await cache.StringSetAsync(holdKey, conflictingToken, TimeSpan.FromMinutes(5));

            var reconciler = new SeatCacheReconciler(context, store, redis, TimeProvider.System);
            var report = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.Report);

            Assert.False(report.RepairApplied);
            Assert.NotEmpty(report.DetectedDrifts);
            Assert.Equal(conflictingToken, (await cache.StringGetAsync(holdKey)).ToString());
            Assert.False(await cache.SetContainsAsync($"quarantined:{eventId}", seat.Id.ToString()));
            Assert.False(await cache.SetContainsAsync($"sold:{eventId}", seat.Id.ToString()));

            await using var verificationContext = CreateContext(connectionString);
            var durableQuote = await verificationContext.HoldQuotes.AsNoTracking().SingleAsync();
            Assert.Equal(HoldQuoteState.Active, durableQuote.State);
            Assert.Null(durableQuote.TerminalAt);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Quarantine_mode_rebuilds_safe_allocations_from_an_empty_cache_and_contains_conflict()
    {
        var databaseName = $"phase12_quarantine_wipe_{Guid.NewGuid():N}";
        var connectionString = DatabaseConnection(databaseName);
        await CreateDatabaseAsync(databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var conflictSeat = Seat.Create(eventId, sectionId, "WIPE-CONFLICT");
            var heldSeat = Seat.Create(eventId, sectionId, "WIPE-HELD");
            var confirmedSeat = Seat.Create(eventId, sectionId, "WIPE-CONFIRMED");
            conflictSeat.MarkSold();
            confirmedSeat.MarkSold();
            var conflictingQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [conflictSeat.Id], 90_000m, "VND", 10, 71,
                now, now.AddMinutes(10));
            var heldQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [heldSeat.Id], 100_000m, "VND", 10, 72,
                now, now.AddMinutes(10));
            var consumedQuote = HoldQuote.Create(
                Guid.NewGuid(), eventId, ownerId, [confirmedSeat.Id], 120_000m, "VND", 10, 73,
                now, now.AddMinutes(10));
            consumedQuote.Consume(orderId, now.AddSeconds(1));
            var allocation = SeatAllocation.CreateConfirmed(
                confirmedSeat.Id,
                eventId,
                consumedQuote.Id,
                orderId,
                ownerId,
                consumedQuote.Generation,
                consumedQuote.PriceSnapshotVersion,
                consumedQuote.Amount,
                consumedQuote.Currency,
                now.AddSeconds(1));

            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            context.Seats.AddRange(conflictSeat, heldSeat, confirmedSeat);
            context.HoldQuotes.AddRange(conflictingQuote, heldQuote, consumedQuote);
            context.SeatAllocations.Add(allocation);
            await context.SaveChangesAsync();

            var store = new RedisSeatHoldStore(redis);
            var reconciler = new SeatCacheReconciler(context, store, redis, TimeProvider.System);
            var result = await reconciler.ReconcileAsync(
                eventId,
                SeatCacheReconcileMode.QuarantineConflict);

            Assert.True(result.RepairApplied);
            Assert.Equal([conflictSeat.Id], result.QuarantinedSeatIds);
            Assert.All(result.RemainingDrifts, drift => Assert.Equal(conflictSeat.Id, drift.SeatId));
            var statuses = await store.GetLiveStatusesAsync(
                eventId,
                [conflictSeat.Id, heldSeat.Id, confirmedSeat.Id]);
            Assert.Equal(SeatStatus.Sold, statuses[conflictSeat.Id]);
            Assert.Equal(SeatStatus.Held, statuses[heldSeat.Id]);
            Assert.Equal(SeatStatus.Sold, statuses[confirmedSeat.Id]);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Repair_reloads_authority_and_removes_a_hold_rebuilt_from_a_stale_snapshot()
    {
        var databaseName = $"phase12_stale_snapshot_{Guid.NewGuid():N}";
        var connectionString = DatabaseConnection(databaseName);
        await CreateDatabaseAsync(databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var seat = Seat.Create(eventId, sectionId, "STALE-SNAPSHOT");
            var quote = HoldQuote.Create(
                Guid.NewGuid(), eventId, Guid.NewGuid(), [seat.Id], 100_000m, "VND", 12, 81,
                now, now.AddMinutes(10));

            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            context.Seats.Add(seat);
            context.HoldQuotes.Add(quote);
            await context.SaveChangesAsync();

            var redisStore = new RedisSeatHoldStore(redis);
            var interleavingStore = new InterleavingSeatHoldStore(
                redisStore,
                async cancellationToken =>
                {
                    await using var releaseContext = CreateContext(connectionString);
                    var releasedQuote = await releaseContext.HoldQuotes.SingleAsync(cancellationToken);
                    releasedQuote.Invalidate(DateTimeOffset.UtcNow);
                    await releaseContext.SaveChangesAsync(cancellationToken);
                });
            var reconciler = new SeatCacheReconciler(
                context,
                interleavingStore,
                redis,
                TimeProvider.System);

            var result = await reconciler.ReconcileAsync(eventId, SeatCacheReconcileMode.RepairCache);

            Assert.True(result.RepairApplied);
            Assert.Empty(result.RemainingDrifts);
            Assert.True(interleavingStore.RebuildCalls >= 1);
            Assert.False(await redis.GetDatabase().KeyExistsAsync($"hold:{eventId}:{seat.Id}"));
            var statuses = await redisStore.GetLiveStatusesAsync(eventId, [seat.Id]);
            Assert.Equal(SeatStatus.Available, statuses[seat.Id]);

            await using var verificationContext = CreateContext(connectionString);
            var durableQuote = await verificationContext.HoldQuotes.AsNoTracking().SingleAsync();
            Assert.Equal(HoldQuoteState.Invalidated, durableQuote.State);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    private static void AssertMissingInventoryAmbiguity(
        SeatCacheReconcileResult result,
        Guid seatId,
        bool remaining = false)
    {
        var drifts = remaining ? result.RemainingDrifts : result.DetectedDrifts;
        Assert.Contains(
            drifts,
            drift => drift.SeatId == seatId &&
                drift.Kind == SeatCacheDriftKind.AmbiguousAuthority &&
                drift.IsOwnershipConflict &&
                drift.Details.Contains("absent from PostgreSQL inventory", StringComparison.Ordinal));
    }

    private static void AssertOwnershipTokenMismatch(
        SeatCacheReconcileResult result,
        Guid seatId,
        bool remaining = false)
    {
        var drifts = remaining ? result.RemainingDrifts : result.DetectedDrifts;
        Assert.Contains(
            drifts,
            drift => drift.SeatId == seatId &&
                drift.Kind == SeatCacheDriftKind.TokenMismatch &&
                drift.IsOwnershipConflict);
    }

    private string DatabaseConnection(string databaseName) => new NpgsqlConnectionStringBuilder(
        environment.PostgresConnectionString)
    {
        Database = databaseName,
    }.ConnectionString;

    private static ReservationDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<ReservationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private async Task CreateDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class InterleavingSeatHoldStore(
        ISeatHoldStore inner,
        Func<CancellationToken, Task> beforeFirstRebuild) : ISeatHoldStore
    {
        private int _rebuildCalls;

        public int RebuildCalls => _rebuildCalls;

        public Task LoadInventoryAsync(
            Guid eventId,
            IReadOnlyList<Guid> seatIds,
            CancellationToken cancellationToken = default) =>
            inner.LoadInventoryAsync(eventId, seatIds, cancellationToken);

        public Task<IReadOnlyList<Guid>> TryHoldAsync(
            Guid eventId,
            IReadOnlyList<Guid> seatIds,
            Guid holdId,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            inner.TryHoldAsync(eventId, seatIds, holdId, ttl, cancellationToken);

        public Task<IReadOnlyList<Guid>> ReleaseAsync(
            Guid eventId,
            Guid holdId,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseAsync(eventId, holdId, cancellationToken);

        public Task<IReadOnlyList<Guid>> ConfirmAsync(
            Guid eventId,
            Guid holdId,
            CancellationToken cancellationToken = default) =>
            inner.ConfirmAsync(eventId, holdId, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(
            Guid eventId,
            IReadOnlyList<Guid> seatIds,
            CancellationToken cancellationToken = default) =>
            inner.GetLiveStatusesAsync(eventId, seatIds, cancellationToken);

        // Every authority-aware call forwards to the real store; a decorator that quietly answered
        // one of them itself would be measuring the decorator, not Redis.
        public Task<HoldStoreResult> TryHoldAsync(
            HoldStoreRequest request,
            CancellationToken cancellationToken = default) =>
            inner.TryHoldAsync(request, cancellationToken);

        public Task<ReleaseHoldStoreResult> ReleaseAsync(
            ReleaseHoldStoreRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseAsync(request, cancellationToken);

        public Task<ConfirmHoldStoreResult> ConfirmAsync(
            ConfirmHoldStoreRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ConfirmAsync(request, cancellationToken);

        public Task<ReleaseAllocationStoreResult> ReleaseAllocationAsync(
            ReleaseAllocationStoreRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseAllocationAsync(request, cancellationToken);

        public Task RestoreAsync(
            RestoreSeatAllocationRequest request,
            CancellationToken cancellationToken = default) =>
            inner.RestoreAsync(request, cancellationToken);

        public async Task<RebuildSeatCacheResult> RebuildAsync(
            RebuildSeatCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _rebuildCalls) == 1)
                await beforeFirstRebuild(cancellationToken);

            return await inner.RebuildAsync(request, cancellationToken);
        }
    }
}
