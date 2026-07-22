using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using TicketFlow.Phase12.HardeningTests.Infrastructure;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Domain.Allocations;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.SalePolicies;
using TicketFlow.Reservation.Domain.Seats;
using TicketFlow.Reservation.Infrastructure;
using TicketFlow.Reservation.Infrastructure.Redis;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class ReservationDurabilityIntegrationTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Redis_confirm_survives_a_crash_before_the_final_Postgres_commit_and_retries_exactly_once()
    {
        var databaseName = $"phase12_reservation_crash_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var holdId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var ownerUserId = Guid.NewGuid();
            const long generation = 41;
            const long priceSnapshotVersion = 12;
            const decimal amount = 240_000m;
            var seats = new[]
            {
                Seat.Create(eventId, sectionId, "VIP-1"),
                Seat.Create(eventId, sectionId, "VIP-2"),
            };
            var seatIds = seats.Select(seat => seat.Id).Order().ToArray();
            var expiresAt = now.AddMinutes(10);
            var quote = HoldQuote.Create(
                holdId,
                eventId,
                ownerUserId,
                seatIds,
                amount,
                "VND",
                priceSnapshotVersion,
                generation,
                now,
                expiresAt);
            var policy = EventSalePolicy.FromSnapshot(
                eventId,
                "Published",
                now.AddHours(-1),
                now.AddHours(1),
                priceSnapshotVersion,
                now,
                [(sectionId, amount / seats.Length, "VND")]);

            await using (var seedContext = CreateContext(connectionString))
            {
                await seedContext.Database.MigrateAsync();
                seedContext.Seats.AddRange(seats);
                seedContext.HoldQuotes.Add(quote);
                seedContext.EventSalePolicies.Add(policy);
                await seedContext.SaveChangesAsync();
            }

            var redisStore = new RedisSeatHoldStore(redis);
            await redisStore.LoadInventoryAsync(eventId, seatIds);
            var held = await redisStore.TryHoldAsync(
                new HoldStoreRequest(
                    eventId,
                    seatIds,
                    holdId,
                    ownerUserId,
                    generation,
                    expiresAt - now));
            Assert.Equal(HoldStoreOutcome.Held, held.Outcome);

            var request = new ConfirmSeatAllocationRequest(
                orderId,
                eventId,
                holdId,
                seatIds,
                ownerUserId,
                generation,
                priceSnapshotVersion,
                amount,
                "VND",
                $"confirm:{orderId:N}");
            var publisher = new RecordingDurableAvailabilityPublisher();

            // MassTransit's EF outbox wraps the consumer in an outer transaction. Save #1 is
            // therefore only flushed, not committed; a crash after Redis must roll everything
            // local back and still be recoverable from the exact cache confirmation marker.
            await using (var crashContext = CreateContext(connectionString))
            {
                await using var outerTransaction = await crashContext.Database.BeginTransactionAsync();
                var observedStore = new ObservedSeatHoldStore(redisStore);
                var service = CreateService(
                    crashContext,
                    observedStore,
                    new FailBeforeSaveUnitOfWork(crashContext, failOnSaveNumber: 2),
                    publisher);

                await Assert.ThrowsAsync<SimulatedProcessCrashException>(() => service.ConfirmAsync(request));

                Assert.Equal([ConfirmHoldStoreOutcome.Applied], observedStore.ConfirmOutcomes);
                await outerTransaction.RollbackAsync();
            }

            await using (var crashBoundaryContext = CreateContext(connectionString))
            {
                Assert.Empty(await crashBoundaryContext.ReservationOperationReceipts.AsNoTracking().ToListAsync());
                Assert.Empty(await crashBoundaryContext.SeatAllocations.AsNoTracking().ToListAsync());

                var activeQuote = await crashBoundaryContext.HoldQuotes.AsNoTracking().SingleAsync();
                Assert.Equal(HoldQuoteState.Active, activeQuote.State);
                Assert.Null(activeQuote.OrderId);
                Assert.Null(activeQuote.TerminalAt);
                Assert.All(
                    await crashBoundaryContext.Seats.AsNoTracking().ToListAsync(),
                    seat => Assert.Equal(SeatStatus.Available, seat.Status));
            }

            ConfirmSeatAllocationResult retry;
            ConfirmSeatAllocationResult replay;
            await using (var retryContext = CreateContext(connectionString))
            {
                var observedStore = new ObservedSeatHoldStore(redisStore);
                var service = CreateService(
                    retryContext,
                    observedStore,
                    new UnitOfWork(retryContext),
                    publisher);

                retry = await service.ConfirmAsync(request);

                Assert.True(retry.Succeeded);
                Assert.NotEqual(Guid.Empty, retry.AllocationReference);
                Assert.Equal(seatIds, retry.SeatIds.Order().ToArray());
                Assert.Equal([ConfirmHoldStoreOutcome.AlreadyAppliedSame], observedStore.ConfirmOutcomes);

                var publishCountBeforeReplay = publisher.Published.Count;
                var cacheConfirmCountBeforeReplay = observedStore.ConfirmOutcomes.Count;
                replay = await service.ConfirmAsync(request);

                AssertEquivalent(retry, replay);
                Assert.Equal(publishCountBeforeReplay, publisher.Published.Count);
                Assert.Equal(cacheConfirmCountBeforeReplay, observedStore.ConfirmOutcomes.Count);
                Assert.False(retryContext.ChangeTracker.HasChanges());
            }

            await using (var verifiedContext = CreateContext(connectionString))
            {
                var durableQuote = await verifiedContext.HoldQuotes.AsNoTracking().SingleAsync();
                Assert.Equal(HoldQuoteState.Consumed, durableQuote.State);
                Assert.Equal(orderId, durableQuote.OrderId);
                Assert.NotNull(durableQuote.TerminalAt);

                var durableReceipt = await verifiedContext.ReservationOperationReceipts
                    .AsNoTracking()
                    .SingleAsync();
                Assert.Equal(retry.AllocationReference, durableReceipt.Id);
                Assert.False(durableReceipt.IsPending);
                Assert.NotNull(durableReceipt.CompletedAt);
                AssertEquivalent(retry, MapReceipt(durableReceipt));

                var allocations = await verifiedContext.SeatAllocations
                    .AsNoTracking()
                    .OrderBy(allocation => allocation.SeatId)
                    .ToListAsync();
                Assert.Equal(seatIds, allocations.Select(allocation => allocation.SeatId).ToArray());
                Assert.All(allocations, allocation =>
                {
                    Assert.Equal(SeatAllocationState.Confirmed, allocation.State);
                    Assert.Equal(eventId, allocation.EventId);
                    Assert.Equal(holdId, allocation.HoldId);
                    Assert.Equal(orderId, allocation.OrderId);
                    Assert.Equal(ownerUserId, allocation.OwnerUserId);
                    Assert.Equal(generation, allocation.Generation);
                });
                Assert.All(
                    await verifiedContext.Seats.AsNoTracking().ToListAsync(),
                    seat => Assert.Equal(SeatStatus.Sold, seat.Status));
            }

            var redisStatuses = await redisStore.GetLiveStatusesAsync(eventId, seatIds);
            Assert.All(seatIds, seatId => Assert.Equal(SeatStatus.Sold, redisStatuses[seatId]));
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_release_and_confirm_converge_to_one_terminal_authority_without_cache_split_brain()
    {
        var databaseName = $"phase12_release_confirm_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var holdId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var ownerUserId = Guid.NewGuid();
            const long generation = 73;
            const long priceSnapshotVersion = 14;
            const decimal amount = 180_000m;
            var seat = Seat.Create(eventId, sectionId, "RACE-1");
            var seatIds = new[] { seat.Id };
            var expiresAt = now.AddMinutes(10);

            await using (var seedContext = CreateContext(connectionString))
            {
                await seedContext.Database.MigrateAsync();
                seedContext.Seats.Add(seat);
                seedContext.HoldQuotes.Add(HoldQuote.Create(
                    holdId,
                    eventId,
                    ownerUserId,
                    seatIds,
                    amount,
                    "VND",
                    priceSnapshotVersion,
                    generation,
                    now,
                    expiresAt));
                seedContext.EventSalePolicies.Add(EventSalePolicy.FromSnapshot(
                    eventId,
                    "Published",
                    now.AddHours(-1),
                    now.AddHours(1),
                    priceSnapshotVersion,
                    now,
                    [(sectionId, amount, "VND")]));
                await seedContext.SaveChangesAsync();
            }

            var redisStore = new RedisSeatHoldStore(redis);
            await redisStore.LoadInventoryAsync(eventId, seatIds);
            var holdResult = await redisStore.TryHoldAsync(new HoldStoreRequest(
                eventId,
                seatIds,
                holdId,
                ownerUserId,
                generation,
                expiresAt - now));
            Assert.Equal(HoldStoreOutcome.Held, holdResult.Outcome);

            await using var confirmContext = CreateContext(connectionString);
            await using var releaseContext = CreateContext(connectionString);
            var confirmService = CreateService(
                confirmContext,
                redisStore,
                new UnitOfWork(confirmContext),
                new RecordingDurableAvailabilityPublisher());
            var releaseService = CreateService(
                releaseContext,
                redisStore,
                new UnitOfWork(releaseContext),
                new RecordingDurableAvailabilityPublisher());
            var request = new ConfirmSeatAllocationRequest(
                orderId,
                eventId,
                holdId,
                seatIds,
                ownerUserId,
                generation,
                priceSnapshotVersion,
                amount,
                "VND",
                $"confirm-race:{orderId:N}");

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmTask = Task.Run(async () =>
            {
                await start.Task;
                return await confirmService.ConfirmAsync(request);
            });
            var releaseTask = Task.Run(async () =>
            {
                await start.Task;
                return await releaseService.ReleaseOwnedHoldAsync(holdId, ownerUserId);
            });
            start.SetResult();

            await Task.WhenAll(confirmTask, releaseTask);
            var confirmation = await confirmTask;
            var released = await releaseTask;

            await using var verifiedContext = CreateContext(connectionString);
            var durableQuote = await verifiedContext.HoldQuotes.AsNoTracking().SingleAsync();
            var allocations = await verifiedContext.SeatAllocations.AsNoTracking().ToListAsync();
            var liveStatus = await redisStore.GetLiveStatusesAsync(eventId, seatIds);

            switch (durableQuote.State)
            {
                case HoldQuoteState.Consumed:
                    Assert.True(confirmation.Succeeded);
                    Assert.False(released);
                    Assert.Equal(orderId, durableQuote.OrderId);
                    var allocation = Assert.Single(allocations);
                    Assert.Equal(SeatAllocationState.Confirmed, allocation.State);
                    Assert.Equal(seat.Id, allocation.SeatId);
                    Assert.Equal(holdId, allocation.HoldId);
                    Assert.Equal(orderId, allocation.OrderId);
                    Assert.Equal(ownerUserId, allocation.OwnerUserId);
                    Assert.Equal(generation, allocation.Generation);
                    Assert.Equal(SeatStatus.Sold, liveStatus[seat.Id]);
                    break;

                case HoldQuoteState.Invalidated:
                    Assert.False(confirmation.Succeeded);
                    Assert.True(released);
                    Assert.Null(durableQuote.OrderId);
                    Assert.Empty(allocations);
                    Assert.Equal(SeatStatus.Available, liveStatus[seat.Id]);
                    break;

                default:
                    Assert.Fail($"Race left the quote in non-terminal state '{durableQuote.State}'.");
                    break;
            }
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_holds_cannot_confirm_the_same_order_or_mutate_the_losing_cache_owner()
    {
        var databaseName = $"phase12_same_order_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var ownerUserId = Guid.NewGuid();
            const long priceSnapshotVersion = 19;
            const decimal amount = 160_000m;
            var seats = new[]
            {
                Seat.Create(eventId, sectionId, "ORDER-RACE-1"),
                Seat.Create(eventId, sectionId, "ORDER-RACE-2"),
            };
            var quotes = seats.Select((seat, index) => HoldQuote.Create(
                Guid.NewGuid(),
                eventId,
                ownerUserId,
                [seat.Id],
                amount,
                "VND",
                priceSnapshotVersion,
                90 + index,
                now,
                now.AddMinutes(10))).ToArray();

            await using (var seedContext = CreateContext(connectionString))
            {
                await seedContext.Database.MigrateAsync();
                seedContext.Seats.AddRange(seats);
                seedContext.HoldQuotes.AddRange(quotes);
                seedContext.EventSalePolicies.Add(EventSalePolicy.FromSnapshot(
                    eventId,
                    "Published",
                    now.AddHours(-1),
                    now.AddHours(1),
                    priceSnapshotVersion,
                    now,
                    [(sectionId, amount, "VND")]));
                await seedContext.SaveChangesAsync();
            }

            var redisStore = new RedisSeatHoldStore(redis);
            await redisStore.LoadInventoryAsync(eventId, seats.Select(seat => seat.Id).ToArray());
            foreach (var quote in quotes)
            {
                Assert.Equal(HoldStoreOutcome.Held, (await redisStore.TryHoldAsync(new HoldStoreRequest(
                    eventId,
                    quote.SeatIds,
                    quote.Id,
                    quote.OwnerUserId,
                    quote.Generation,
                    quote.ExpiresAt - now))).Outcome);
            }

            await using var firstContext = CreateContext(connectionString);
            await using var secondContext = CreateContext(connectionString);
            var services = new[]
            {
                CreateService(firstContext, redisStore, new UnitOfWork(firstContext), new RecordingDurableAvailabilityPublisher()),
                CreateService(secondContext, redisStore, new UnitOfWork(secondContext), new RecordingDurableAvailabilityPublisher()),
            };
            var requests = quotes.Select((quote, index) => new ConfirmSeatAllocationRequest(
                orderId,
                eventId,
                quote.Id,
                quote.SeatIds,
                ownerUserId,
                quote.Generation,
                priceSnapshotVersion,
                amount,
                "VND",
                $"same-order:{index}:{Guid.NewGuid():N}" )).ToArray();

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = services.Select((service, index) => Task.Run(async () =>
            {
                await start.Task;
                return await service.ConfirmAsync(requests[index]);
            })).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(attempts);

            var winner = Assert.Single(results, result => result.Succeeded);
            var loser = Assert.Single(results, result => !result.Succeeded);
            Assert.Equal("OrderAlreadyAllocated", loser.FailureCode);

            await using var verifiedContext = CreateContext(connectionString);
            var durableQuotes = await verifiedContext.HoldQuotes.AsNoTracking().OrderBy(quote => quote.Id).ToListAsync();
            var consumed = Assert.Single(durableQuotes, quote => quote.State == HoldQuoteState.Consumed);
            var stillActive = Assert.Single(durableQuotes, quote => quote.State == HoldQuoteState.Active);
            Assert.Equal(winner.HoldId, consumed.Id);
            Assert.Equal(orderId, consumed.OrderId);
            Assert.Equal(loser.HoldId, stillActive.Id);
            Assert.Null(stillActive.OrderId);

            var allocation = Assert.Single(await verifiedContext.SeatAllocations.AsNoTracking().ToListAsync());
            Assert.Equal(winner.HoldId, allocation.HoldId);
            Assert.Equal(orderId, allocation.OrderId);
            var statuses = await redisStore.GetLiveStatusesAsync(eventId, seats.Select(seat => seat.Id).ToArray());
            Assert.Equal(SeatStatus.Sold, statuses[allocation.SeatId]);
            Assert.Equal(SeatStatus.Held, statuses[stillActive.SeatIds.Single()]);
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Hold_waits_for_an_exclusive_sale_policy_change_and_cannot_commit_from_a_stale_published_snapshot()
    {
        var databaseName = $"phase12_hold_cancel_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);
        using var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var seat = Seat.Create(eventId, sectionId, "CANCEL-RACE-1");
            await using (var seedContext = CreateContext(connectionString))
            {
                await seedContext.Database.MigrateAsync();
                seedContext.Seats.Add(seat);
                seedContext.EventSalePolicies.Add(EventSalePolicy.FromSnapshot(
                    eventId,
                    "Published",
                    now.AddHours(-1),
                    now.AddHours(1),
                    31,
                    now,
                    [(sectionId, 140_000m, "VND")]));
                await seedContext.SaveChangesAsync();
            }

            var redisStore = new RedisSeatHoldStore(redis);
            await redisStore.LoadInventoryAsync(eventId, [seat.Id]);

            await using var cancelContext = CreateContext(connectionString);
            await using var cancelTransaction = await cancelContext.Database.BeginTransactionAsync();
            await using var policyLock = await new ReservationOperationReceiptRepository(cancelContext)
                .AcquireLockAsync("sale-policy", eventId.ToString("N"));
            var policy = await cancelContext.EventSalePolicies.SingleAsync(item => item.Id == eventId);
            Assert.True(policy.ApplySnapshot(
                "Cancelled",
                now.AddHours(-1),
                now.AddHours(1),
                32,
                now.AddSeconds(1),
                [(sectionId, 140_000m, "VND")]));
            await cancelContext.SaveChangesAsync();

            await using var holdContext = CreateContext(connectionString);
            var holdService = CreateService(
                holdContext,
                redisStore,
                new UnitOfWork(holdContext),
                new RecordingDurableAvailabilityPublisher());
            var holdTask = holdService.HoldAsync(new HoldSeatsRequest(eventId, [seat.Id], ownerId));

            Assert.NotSame(holdTask, await Task.WhenAny(holdTask, Task.Delay(200)));
            await cancelTransaction.CommitAsync();

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => holdTask);
            Assert.Contains("not currently saleable", rejected.Message, StringComparison.OrdinalIgnoreCase);
            await using var verifiedContext = CreateContext(connectionString);
            Assert.Empty(await verifiedContext.HoldQuotes.AsNoTracking().ToListAsync());
            var status = await redisStore.GetLiveStatusesAsync(eventId, [seat.Id]);
            Assert.Equal(SeatStatus.Available, status[seat.Id]);
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
    }

    private static ReservationService CreateService(
        ReservationDbContext context,
        ISeatHoldStore holdStore,
        IUnitOfWork unitOfWork,
        ISeatAvailabilityEventPublisher publisher) =>
        new(
            new SeatRepository(context),
            holdStore,
            new EventSalePolicyRepository(context),
            new HoldQuoteRepository(context),
            new SeatAllocationRepository(context),
            new ReservationOperationReceiptRepository(context),
            new PostgresAllocationGenerationSource(context),
            unitOfWork,
            publisher,
            TimeProvider.System,
            new ReservationOptions());

    private static ReservationDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<ReservationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private static ConfirmSeatAllocationResult MapReceipt(ReservationOperationReceipt receipt)
    {
        var snapshot = receipt.ReplayConfirm(receipt.RequestFingerprint);
        return new ConfirmSeatAllocationResult(
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
    }

    private static void AssertEquivalent(
        ConfirmSeatAllocationResult expected,
        ConfirmSeatAllocationResult actual)
    {
        Assert.Equal(expected.Succeeded, actual.Succeeded);
        Assert.Equal(expected.OrderId, actual.OrderId);
        Assert.Equal(expected.EventId, actual.EventId);
        Assert.Equal(expected.HoldId, actual.HoldId);
        Assert.Equal(expected.OwnerUserId, actual.OwnerUserId);
        Assert.Equal(expected.Generation, actual.Generation);
        Assert.Equal(expected.SeatIds.Order(), actual.SeatIds.Order());
        Assert.Equal(expected.AllocationReference, actual.AllocationReference);
        Assert.Equal(expected.PriceSnapshotVersion, actual.PriceSnapshotVersion);
        Assert.Equal(expected.Amount, actual.Amount);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(expected.DecidedAt, actual.DecidedAt);
        Assert.Equal(expected.FailureCode, actual.FailureCode);
        Assert.Equal(expected.FailureReason, actual.FailureReason);
    }

    private static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
            connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class FailBeforeSaveUnitOfWork(
    ReservationDbContext context,
    int failOnSaveNumber) : IUnitOfWork
{
    private int _saveCount;

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        _saveCount++;
        if (_saveCount == failOnSaveNumber)
            throw new SimulatedProcessCrashException();

        await context.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class SimulatedProcessCrashException()
    : InvalidOperationException("Failpoint: Redis committed before the final Postgres SaveChanges.");

internal sealed class RecordingDurableAvailabilityPublisher : ISeatAvailabilityEventPublisher
{
    public List<(Guid EventId, IReadOnlyList<Guid> SeatIds, string Status, long Generation)> Published { get; } = [];

    public Task PublishAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        long generation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        Published.Add((eventId, seatIds, status, generation));
        return Task.CompletedTask;
    }
}

internal sealed class ObservedSeatHoldStore(ISeatHoldStore inner) : ISeatHoldStore
{
    public List<ConfirmHoldStoreOutcome> ConfirmOutcomes { get; } = [];

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

    public Task<HoldStoreResult> TryHoldAsync(
        HoldStoreRequest request,
        CancellationToken cancellationToken = default) =>
        inner.TryHoldAsync(request, cancellationToken);

    public Task<IReadOnlyList<Guid>> ReleaseAsync(
        Guid eventId,
        Guid holdId,
        CancellationToken cancellationToken = default) =>
        inner.ReleaseAsync(eventId, holdId, cancellationToken);

    public Task<ReleaseHoldStoreResult> ReleaseAsync(
        ReleaseHoldStoreRequest request,
        CancellationToken cancellationToken = default) =>
        inner.ReleaseAsync(request, cancellationToken);

    public Task<IReadOnlyList<Guid>> ConfirmAsync(
        Guid eventId,
        Guid holdId,
        CancellationToken cancellationToken = default) =>
        inner.ConfirmAsync(eventId, holdId, cancellationToken);

    public async Task<ConfirmHoldStoreResult> ConfirmAsync(
        ConfirmHoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.ConfirmAsync(request, cancellationToken);
        ConfirmOutcomes.Add(result.Outcome);
        return result;
    }

    public Task RestoreAsync(
        RestoreSeatAllocationRequest request,
        CancellationToken cancellationToken = default) =>
        inner.RestoreAsync(request, cancellationToken);

    public Task<RebuildSeatCacheResult> RebuildAsync(
        RebuildSeatCacheRequest request,
        CancellationToken cancellationToken = default) =>
        inner.RebuildAsync(request, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        inner.GetLiveStatusesAsync(eventId, seatIds, cancellationToken);

    public Task RemoveFromSoldAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        inner.RemoveFromSoldAsync(eventId, seatIds, cancellationToken);
}
