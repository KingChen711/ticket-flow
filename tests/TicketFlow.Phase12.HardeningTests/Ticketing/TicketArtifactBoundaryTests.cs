using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Contracts.Messaging.Workflows;
using TicketFlow.Messaging;
using TicketFlow.Phase12.HardeningTests.Support;
using TicketFlow.Ticketing.Api.Consumers;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Phase12.HardeningTests.Ticketing;

public sealed class TicketArtifactBoundaryTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Lost_response_after_PDF_upload_must_not_upload_the_same_ticket_twice_on_retry()
    {
        var sideEffect = new ControlledSideEffect<string>(
            () => "deterministic/object.pdf",
            ControlledSideEffectOutcome.SideEffectSucceededButResponseLost);

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITicketRepository, EmptyTicketRepositoryFake>()
            .AddSingleton<ITicketIssuanceJobRepository, RecordingIssuanceJobRepositoryFake>()
            .AddSingleton<TicketFlow.Ticketing.Application.Abstractions.IUnitOfWork, NoopTicketUnitOfWorkFake>()
            .AddSingleton<ITicketPdfRenderer>(new ControlledTicketRendererFake(sideEffect))
            .AddSingleton(TimeProvider.System)
            .AddMassTransitTestHarness(configuration => configuration.AddConsumer<IssueTicketsConsumer>())
            .BuildServiceProvider(validateScopes: true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var orderId = Guid.NewGuid();
        var command = new IssueTickets(
            orderId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [Guid.NewGuid()],
            "Buyer",
            "buyer@example.com");

        // The upload succeeds; only its response is lost. Before Commit 10 that ambiguity was
        // published as TicketingFailed, telling the saga to refund a customer whose PDFs existed.
        await harness.Bus.Publish(command, context => context.MessageId = Guid.NewGuid());
        Assert.True(await sideEffect.WaitForSuccessfulSideEffectsAsync(1, TimeSpan.FromSeconds(10)));
        Assert.True(await harness.Consumed.Any<IssueTickets>(x => x.Context.Message.OrderId == orderId));

        Assert.Empty(harness.Published.Select<TicketingFailed>());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_transient_renderer_fault_must_be_retried_and_never_fail_the_order()
    {
        var sideEffect = new ControlledSideEffect<string>(
            () => throw new InvalidOperationException("MinIO is unreachable."));

        await using var provider = BuildProvider(sideEffect);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var orderId = Guid.NewGuid();

        await harness.Bus.Publish(Command(orderId), context => context.MessageId = Guid.NewGuid());
        Assert.True(await harness.Consumed.Any<IssueTickets>(x => x.Context.Message.OrderId == orderId));

        // Refunding on a storage blip is the expensive mistake; the message is left to redelivery.
        Assert.Empty(harness.Published.Select<TicketingFailed>());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_permanent_refusal_must_fail_the_order_exactly_once()
    {
        var sideEffect = new ControlledSideEffect<string>(
            () => throw new PermanentMessageFaultException("This seat has no renderable layout."));

        await using var provider = BuildProvider(sideEffect);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var orderId = Guid.NewGuid();

        await harness.Bus.Publish(Command(orderId), context => context.MessageId = Guid.NewGuid());
        Assert.True(await harness.Published.Any<TicketingFailed>(x => x.Context.Message.OrderId == orderId));

        // A refusal that retrying cannot fix must reach the saga so compensation can run — the
        // mirror image of the transient case, and the reason the two are told apart at all.
        Assert.Single(harness.Published.Select<TicketingFailed>());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_late_permanent_failure_in_a_multi_ticket_batch_must_not_persist_or_replay_a_partial_success()
    {
        var repository = new EmptyTicketRepositoryFake();
        var jobs = new RecordingIssuanceJobRepositoryFake();
        var renderer = new FailOnSecondTicketRenderer();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITicketRepository>(repository)
            .AddSingleton<ITicketIssuanceJobRepository>(jobs)
            .AddSingleton<TicketFlow.Ticketing.Application.Abstractions.IUnitOfWork, NoopTicketUnitOfWorkFake>()
            .AddSingleton<ITicketPdfRenderer>(renderer)
            .AddSingleton(TimeProvider.System)
            .AddMassTransitTestHarness(configuration => configuration.AddConsumer<IssueTicketsConsumer>())
            .BuildServiceProvider(validateScopes: true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var command = new IssueTickets(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            [Guid.NewGuid(), Guid.NewGuid()],
            "Buyer",
            "buyer@example.com");
        await harness.Bus.Publish(command, context => context.MessageId = Guid.NewGuid());
        Assert.True(await harness.Published.Any<TicketingFailed>(x =>
            x.Context.Message.OrderId == command.OrderId));

        Assert.Equal(2, renderer.Attempts);
        Assert.Empty(repository.Tickets);
        Assert.Empty(harness.Published.Select<TicketsIssued>());

        // A semantic duplicate observes the durable Failed job before looking at any legacy rows.
        // It may replay the failure, but it must never turn a subset into TicketsIssued.
        await harness.Bus.Publish(command, context => context.MessageId = Guid.NewGuid());
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => harness.Consumed.Select<IssueTickets>().Count() >= 2,
            TimeSpan.FromSeconds(10)));
        Assert.Empty(repository.Tickets);
        Assert.Empty(harness.Published.Select<TicketsIssued>());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_legacy_partial_batch_renders_only_missing_seats_and_completes_the_durable_job()
    {
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var existingSeatId = Guid.NewGuid();
        var missingSeatId = Guid.NewGuid();
        var repository = new EmptyTicketRepositoryFake();
        repository.Add(Ticket.Issue(
            orderId,
            existingSeatId,
            $"{orderId}:{existingSeatId}",
            $"{orderId}/{existingSeatId}.pdf",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            customerId,
            eventId));
        var jobs = new RecordingIssuanceJobRepositoryFake();
        var job = TicketIssuanceJob.Claim(
            orderId,
            eventId,
            customerId,
            [existingSeatId, missingSeatId],
            "Buyer",
            "buyer@example.com",
            operationKey: null,
            priceSnapshotVersion: 0,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        jobs.Add(job);
        var render = new ControlledSideEffect<string>(() => $"{orderId}/{missingSeatId}.pdf");

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITicketRepository>(repository)
            .AddSingleton<ITicketIssuanceJobRepository>(jobs)
            .AddSingleton<TicketFlow.Ticketing.Application.Abstractions.IUnitOfWork, NoopTicketUnitOfWorkFake>()
            .AddSingleton<ITicketPdfRenderer>(new ControlledTicketRendererFake(render))
            .AddSingleton(TimeProvider.System)
            .AddMassTransitTestHarness(configuration => configuration.AddConsumer<IssueTicketsConsumer>())
            .BuildServiceProvider(validateScopes: true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(
            new IssueTickets(
                orderId,
                customerId,
                eventId,
                [existingSeatId, missingSeatId],
                "Buyer",
                "buyer@example.com"),
            context => context.MessageId = Guid.NewGuid());

        Assert.True(await harness.Published.Any<TicketsIssued>(published =>
            published.Context.Message.OrderId == orderId &&
            published.Context.Message.TicketPdfUrls.Count == 2));
        Assert.Equal(1, render.Attempts);
        Assert.Equal(2, repository.Tickets.Count);
        Assert.Equal(TicketIssuanceJobState.Issued, job.State);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Cancellation_tombstone_prevents_late_ticket_issuance_and_acknowledges_zero_tickets()
    {
        var render = new ControlledSideEffect<string>(() => "should-not-render.pdf");
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITicketRepository, EmptyTicketRepositoryFake>()
            .AddSingleton<ITicketIssuanceJobRepository, RecordingIssuanceJobRepositoryFake>()
            .AddSingleton<TicketFlow.Ticketing.Application.Abstractions.IUnitOfWork, NoopTicketUnitOfWorkFake>()
            .AddSingleton<ITicketPdfRenderer>(new ControlledTicketRendererFake(render))
            .AddSingleton(TimeProvider.System)
            .AddMassTransitTestHarness(configuration =>
            {
                configuration.AddConsumer<RevokeTicketsConsumer>();
                configuration.AddConsumer<IssueTicketsConsumer>();
            })
            .BuildServiceProvider(validateScopes: true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var orderId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        await harness.Bus.Publish(new OrderCancelledIntegrationEvent(
            orderId,
            userId,
            "Event cancelled.",
            DateTimeOffset.UtcNow,
            eventId,
            workflowId,
            "Buyer",
            "buyer@example.com"));
        Assert.True(await harness.Published.Any<TicketsRevokedForEventCancellation>(x =>
            x.Context.Message.WorkflowId == workflowId &&
            x.Context.Message.OrderId == orderId &&
            x.Context.Message.TicketCount == 0));

        await harness.Bus.Publish(new IssueTickets(
            orderId,
            userId,
            eventId,
            [Guid.NewGuid()],
            "Buyer",
            "buyer@example.com"));
        Assert.True(await harness.Consumed.Any<IssueTickets>(x => x.Context.Message.OrderId == orderId));

        Assert.Equal(0, render.Attempts);
        Assert.Empty(harness.Published.Select<TicketsIssued>());
    }

    private static IssueTickets Command(Guid orderId) => new(
        orderId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        [Guid.NewGuid()],
        "Buyer",
        "buyer@example.com");

    private static ServiceProvider BuildProvider(ControlledSideEffect<string> sideEffect) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITicketRepository, EmptyTicketRepositoryFake>()
            .AddSingleton<ITicketIssuanceJobRepository, RecordingIssuanceJobRepositoryFake>()
            .AddSingleton<TicketFlow.Ticketing.Application.Abstractions.IUnitOfWork, NoopTicketUnitOfWorkFake>()
            .AddSingleton<ITicketPdfRenderer>(new ControlledTicketRendererFake(sideEffect))
            .AddSingleton(TimeProvider.System)
            .AddMassTransitTestHarness(configuration => configuration.AddConsumer<IssueTicketsConsumer>())
            .BuildServiceProvider(validateScopes: true);
}

internal sealed class RecordingIssuanceJobRepositoryFake : ITicketIssuanceJobRepository
{
    private readonly Dictionary<Guid, TicketIssuanceJob> _jobs = [];

    public Task<TicketIssuanceJob?> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        lock (_jobs)
            return Task.FromResult(_jobs.GetValueOrDefault(orderId));
    }

    public void Add(TicketIssuanceJob job)
    {
        lock (_jobs)
            _jobs[job.OrderId] = job;
    }

    public Task<IReadOnlyList<TicketIssuanceJob>> ListRecoverableAsync(
        DateTimeOffset attemptCutoff,
        DateTimeOffset recoveryCutoff,
        int take,
        CancellationToken cancellationToken = default)
    {
        lock (_jobs)
        {
            IReadOnlyList<TicketIssuanceJob> result = _jobs.Values
                .Where(job =>
                    job.State == TicketIssuanceJobState.Pending &&
                    job.LastAttemptAt <= attemptCutoff &&
                    (job.LastRecoveryAt is null || job.LastRecoveryAt <= recoveryCutoff) &&
                    job.HasRecoveryIntent)
                .Take(take)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<TicketIssuanceJob>> ListTerminalArtifactsAsync(
        DateTimeOffset completionCutoff,
        int take,
        CancellationToken cancellationToken = default)
    {
        lock (_jobs)
        {
            IReadOnlyList<TicketIssuanceJob> result = _jobs.Values
                .Where(job =>
                    job.State is TicketIssuanceJobState.Failed or TicketIssuanceJobState.Cancelled &&
                    job.CompletedAt <= completionCutoff &&
                    job.ArtifactsReconciledAt is null)
                .Take(take)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public bool IsUniqueViolation(Exception exception) => false;
}

internal sealed class EmptyTicketRepositoryFake : ITicketRepository
{
    private readonly List<Ticket> _tickets = [];

    public IReadOnlyList<Ticket> Tickets => _tickets;

    public Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Ticket>>(_tickets.Where(ticket => ticket.OrderId == orderId).ToList());

    public Task<Ticket?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tickets.SingleOrDefault(ticket => ticket.Id == id));

    public Task<Ticket?> GetByQrPayloadAsync(string qrPayload, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tickets.SingleOrDefault(ticket => ticket.QrPayload == qrPayload));

    public Task<bool> TryClaimCheckInAsync(
        Guid ticketId,
        Guid expectedEventId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    public void Add(Ticket ticket)
    {
        _tickets.Add(ticket);
    }
}

internal sealed class NoopTicketUnitOfWorkFake : TicketFlow.Ticketing.Application.Abstractions.IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FailOnSecondTicketRenderer : ITicketPdfRenderer
{
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public Task<string> RenderAndStoreAsync(
        TicketPdfModel model,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _attempts) == 2)
        {
            throw new PermanentMessageFaultException(
                "The second seat has no renderable layout.");
        }

        return Task.FromResult(objectKey);
    }
}
