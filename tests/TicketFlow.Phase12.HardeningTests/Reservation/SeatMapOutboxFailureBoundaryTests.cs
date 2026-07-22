using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Phase12.HardeningTests.Infrastructure;
using TicketFlow.Reservation.Api.Consumers;
using TicketFlow.Reservation.Api.Messaging;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Domain.Allocations;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.SalePolicies;
using TicketFlow.Reservation.Domain.Seats;
using TicketFlow.Reservation.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class SeatMapOutboxFailureBoundaryTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task SignalR_relay_failure_happens_after_durable_confirmation_and_cannot_roll_it_back()
    {
        var databaseName = $"phase12_seatmap_outbox_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var holdId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var ownerUserId = Guid.NewGuid();
            const long generation = 23;
            const long priceSnapshotVersion = 17;
            const decimal amount = 180_000m;
            var seat = Seat.Create(eventId, sectionId, "OUTBOX-1");
            var quote = HoldQuote.Create(
                holdId,
                eventId,
                ownerUserId,
                [seat.Id],
                amount,
                "VND",
                priceSnapshotVersion,
                generation,
                now,
                now.AddMinutes(10));
            var policy = EventSalePolicy.FromSnapshot(
                eventId,
                "Published",
                now.AddHours(-1),
                now.AddHours(1),
                priceSnapshotVersion,
                now,
                [(sectionId, amount, "VND")]);
            var dbOptions = new DbContextOptionsBuilder<ReservationDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
            var holdStore = new FakeSeatHoldStore();
            holdStore.SeedTypedHold(eventId, holdId, ownerUserId, generation, [seat.Id]);
            var notifier = new CommitObservingThrowingSeatMapNotifier(dbOptions, holdId);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ReservationDbContext>(options =>
                options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
            services.AddScoped<ISeatRepository, SeatRepository>();
            services.AddScoped<IEventSalePolicyRepository, EventSalePolicyRepository>();
            services.AddScoped<IHoldQuoteRepository, HoldQuoteRepository>();
            services.AddScoped<ISeatAllocationRepository, SeatAllocationRepository>();
            services.AddScoped<IReservationOperationReceiptRepository, ReservationOperationReceiptRepository>();
            services.AddScoped<IAllocationGenerationSource, PostgresAllocationGenerationSource>();
            services.AddScoped<CommitGateUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<CommitGateUnitOfWork>());
            services.AddScoped<ISeatAvailabilityEventPublisher, MassTransitSeatAvailabilityEventPublisher>();
            services.AddScoped<ReservationService>();
            services.AddSingleton<ISeatHoldStore>(holdStore);
            services.AddSingleton<ISeatMapNotifier>(notifier);
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton(new ReservationOptions());
            services.AddMassTransitTestHarness(configuration =>
            {
                configuration.AddEntityFrameworkOutbox<ReservationDbContext>(outbox =>
                {
                    outbox.UsePostgres();
                    outbox.UseBusOutbox();
                });
                configuration.AddConsumer<SeatMapNotificationConsumer>();
            });

            await using var provider = services.BuildServiceProvider(validateScopes: true);
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var dbContext = seedScope.ServiceProvider.GetRequiredService<ReservationDbContext>();
                await dbContext.Database.MigrateAsync();
                dbContext.Seats.Add(seat);
                dbContext.HoldQuotes.Add(quote);
                dbContext.EventSalePolicies.Add(policy);
                await dbContext.SaveChangesAsync();
            }

            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            try
            {
                await using var confirmScope = provider.CreateAsyncScope();
                var service = confirmScope.ServiceProvider.GetRequiredService<ReservationService>();
                var commitGate = confirmScope.ServiceProvider.GetRequiredService<CommitGateUnitOfWork>();
                var request = new ConfirmSeatAllocationRequest(
                    orderId,
                    eventId,
                    holdId,
                    [seat.Id],
                    ownerUserId,
                    generation,
                    priceSnapshotVersion,
                    amount,
                    "VND",
                    $"seatmap-outbox:{orderId:N}");

                var confirmTask = service.ConfirmAsync(request);
                await commitGate.FinalSaveReached.WaitAsync(TimeSpan.FromSeconds(15));

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300));
                    Assert.False(
                        notifier.Observation.IsCompleted,
                        "The SignalR relay must not receive a bus-outbox message before the final durable SaveChanges.");
                }
                finally
                {
                    commitGate.AllowFinalSave();
                }

                var result = await confirmTask;
                Assert.True(result.Succeeded);

                var observation = await notifier.Observation.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(HoldQuoteState.Consumed, observation.QuoteState);
                Assert.Equal(1, observation.ConfirmedAllocationCount);
                Assert.True(observation.ReceiptCompleted);
                Assert.Equal(eventId, observation.EventId);
                Assert.Equal([seat.Id], observation.SeatIds);
                Assert.Equal("Sold", observation.Status);
                Assert.Equal(generation, observation.NotificationGeneration);
                await notifier.FailureTriggered.WaitAsync(TimeSpan.FromSeconds(10));

                await using var verificationContext = new ReservationDbContext(dbOptions);
                var durableQuote = await verificationContext.HoldQuotes.AsNoTracking()
                    .SingleAsync(item => item.Id == holdId);
                var durableAllocation = await verificationContext.SeatAllocations.AsNoTracking()
                    .SingleAsync(item => item.HoldId == holdId);
                var durableReceipt = await verificationContext.ReservationOperationReceipts.AsNoTracking()
                    .SingleAsync();

                Assert.Equal(HoldQuoteState.Consumed, durableQuote.State);
                Assert.Equal(orderId, durableQuote.OrderId);
                Assert.Equal(SeatAllocationState.Confirmed, durableAllocation.State);
                Assert.Equal(orderId, durableAllocation.OrderId);
                Assert.False(durableReceipt.IsPending);
                Assert.NotNull(durableReceipt.CompletedAt);
            }
            finally
            {
                await harness.Stop();
            }
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
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

internal sealed class CommitGateUnitOfWork(ReservationDbContext dbContext) : IUnitOfWork
{
    private readonly TaskCompletionSource _allowFinalSave =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finalSaveReached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _saveCount;

    public Task FinalSaveReached => _finalSaveReached.Task;

    public void AllowFinalSave() => _allowFinalSave.TrySetResult();

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        _saveCount++;
        if (_saveCount == 2)
        {
            _finalSaveReached.TrySetResult();
            await _allowFinalSave.Task.WaitAsync(cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class CommitObservingThrowingSeatMapNotifier(
    DbContextOptions<ReservationDbContext> dbOptions,
    Guid holdId) : ISeatMapNotifier
{
    private readonly TaskCompletionSource<SeatMapCommitObservation> _observation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _failureTriggered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<SeatMapCommitObservation> Observation => _observation.Task;
    public Task FailureTriggered => _failureTriggered.Task;

    public Task SeatsUpdatedAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        CancellationToken cancellationToken = default) =>
        SeatsUpdatedAsync(eventId, seatIds, status, generation: 0, cancellationToken);

    public async Task SeatsUpdatedAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        long generation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = new ReservationDbContext(dbOptions);
            var quoteState = await dbContext.HoldQuotes.AsNoTracking()
                .Where(item => item.Id == holdId)
                .Select(item => item.State)
                .SingleAsync(cancellationToken);
            var confirmedAllocationCount = await dbContext.SeatAllocations.AsNoTracking()
                .CountAsync(
                    item => item.HoldId == holdId && item.State == SeatAllocationState.Confirmed,
                    cancellationToken);
            var receiptCompleted = await dbContext.ReservationOperationReceipts.AsNoTracking()
                .AnyAsync(item => item.CompletedAt != null, cancellationToken);

            _observation.TrySetResult(new SeatMapCommitObservation(
                quoteState,
                confirmedAllocationCount,
                receiptCompleted,
                eventId,
                seatIds.ToArray(),
                status,
                generation));
        }
        catch (Exception exception)
        {
            _observation.TrySetException(exception);
            throw;
        }

        _failureTriggered.TrySetResult();
        throw new InvalidOperationException(
            "Failpoint: SignalR relay failed after the durable confirmation/outbox commit.");
    }
}

internal sealed record SeatMapCommitObservation(
    HoldQuoteState QuoteState,
    int ConfirmedAllocationCount,
    bool ReceiptCompleted,
    Guid EventId,
    IReadOnlyList<Guid> SeatIds,
    string Status,
    long NotificationGeneration);
