using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Ticketing.Api.Services;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Phase12.HardeningTests.Ticketing;

public sealed class TicketIssuanceReconciliationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_stale_pending_job_is_republished_from_its_durable_command_snapshot()
    {
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var seatIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var job = TicketIssuanceJob.Claim(
            orderId,
            eventId,
            customerId,
            seatIds,
            "Buyer",
            "buyer@example.com",
            "issue-operation",
            priceSnapshotVersion: 7,
            Now - TimeSpan.FromMinutes(10));
        var jobs = new RecordingIssuanceJobRepositoryFake();
        jobs.Add(job);
        var publisher = new RecordingRecoveryPublisher();
        var unitOfWork = new RecordingUnitOfWork();
        var service = CreateService(
            jobs,
            new EmptyTicketRepositoryFake(),
            new RecordingObjectStorage(),
            publisher,
            unitOfWork);

        await service.ReconcileOnceAsync();

        var recovery = Assert.Single(publisher.Requests);
        Assert.Equal(orderId, recovery.OrderId);
        Assert.Equal(customerId, recovery.CustomerId);
        Assert.Equal(eventId, recovery.EventId);
        Assert.Equal(seatIds, recovery.SeatIds);
        Assert.Equal("Buyer", recovery.CustomerName);
        Assert.Equal("buyer@example.com", recovery.CustomerEmail);
        Assert.Equal("issue-operation", recovery.OperationKey);
        Assert.Equal(7, recovery.PriceSnapshotVersion);
        Assert.Equal(Now, job.LastRecoveryAt);
        Assert.Equal(1, unitOfWork.SaveCalls);

        // The durable cooldown marker prevents every worker tick from flooding the queue while
        // the first recovery command is still waiting to be consumed.
        await service.ReconcileOnceAsync();
        Assert.Single(publisher.Requests);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_terminal_job_revokes_legacy_partial_rows_and_deletes_only_unreferenced_artifacts()
    {
        var orderId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var persistedSeatId = Guid.NewGuid();
        var orphanSeatId = Guid.NewGuid();
        var persistedKey = $"{orderId}/{persistedSeatId}.pdf";
        var orphanKey = $"{orderId}/{orphanSeatId}.pdf";
        var job = TicketIssuanceJob.Claim(
            orderId,
            eventId,
            customerId,
            [persistedSeatId, orphanSeatId],
            "Buyer",
            "buyer@example.com",
            operationKey: null,
            priceSnapshotVersion: 1,
            Now - TimeSpan.FromMinutes(30));
        job.MarkFailed("The second seat cannot be rendered.", Now - TimeSpan.FromMinutes(20));

        var jobs = new RecordingIssuanceJobRepositoryFake();
        jobs.Add(job);
        var ticketRepository = new EmptyTicketRepositoryFake();
        var partialTicket = Ticket.Issue(
            orderId,
            persistedSeatId,
            $"{orderId}:{persistedSeatId}",
            persistedKey,
            Now - TimeSpan.FromMinutes(30),
            customerId,
            eventId);
        ticketRepository.Add(partialTicket);
        var storage = new RecordingObjectStorage(persistedKey, orphanKey, "another-order/file.pdf");
        var unitOfWork = new RecordingUnitOfWork();
        var service = CreateService(
            jobs,
            ticketRepository,
            storage,
            new RecordingRecoveryPublisher(),
            unitOfWork);

        await service.ReconcileOnceAsync();

        Assert.NotNull(partialTicket.RevokedAt);
        Assert.Equal([orphanKey], storage.DeletedKeys);
        Assert.Equal(Now, job.ArtifactsReconciledAt);
        Assert.Equal(1, unitOfWork.SaveCalls);

        await service.ReconcileOnceAsync();
        Assert.Equal([orphanKey], storage.DeletedKeys);
    }

    private static TicketIssuanceReconciliationService CreateService(
        ITicketIssuanceJobRepository jobs,
        ITicketRepository tickets,
        ITicketObjectStorage storage,
        ITicketIssuanceRecoveryPublisher publisher,
        IUnitOfWork unitOfWork) =>
        new(
            jobs,
            tickets,
            storage,
            publisher,
            unitOfWork,
            new TicketIssuanceReconciliationOptions
            {
                RecoverAfter = TimeSpan.FromMinutes(2),
                RecoveryCooldown = TimeSpan.FromMinutes(2),
                TerminalCleanupDelay = TimeSpan.FromMinutes(10),
            },
            new ReconciliationTimeProvider(),
            NullLogger<TicketIssuanceReconciliationService>.Instance);

    private sealed class ReconciliationTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingRecoveryPublisher : ITicketIssuanceRecoveryPublisher
    {
        public List<TicketIssuanceRecoveryRequest> Requests { get; } = [];

        public Task PublishAsync(
            TicketIssuanceRecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingObjectStorage(params string[] keys) : ITicketObjectStorage
    {
        private readonly List<string> _keys = [.. keys];

        public List<string> DeletedKeys { get; } = [];

        public Task<byte[]> DownloadAsync(
            string objectKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string prefix,
            CancellationToken cancellationToken = default) =>
            // Include all keys deliberately; the service must retain its own prefix guard before
            // issuing destructive calls to an object store.
            Task.FromResult<IReadOnlyList<string>>([.. _keys]);

        public Task DeleteAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(objectKey);
            _keys.Remove(objectKey);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int SaveCalls { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return Task.CompletedTask;
        }
    }
}
