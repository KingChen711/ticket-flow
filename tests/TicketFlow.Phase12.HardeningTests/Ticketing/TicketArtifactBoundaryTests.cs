using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Ticketing;
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

    public bool IsUniqueViolation(Exception exception) => false;
}

internal sealed class EmptyTicketRepositoryFake : ITicketRepository
{
    public Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Ticket>>([]);

    public Task<Ticket?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<Ticket?>(null);

    public Task<Ticket?> GetByQrPayloadAsync(string qrPayload, CancellationToken cancellationToken = default) =>
        Task.FromResult<Ticket?>(null);

    public Task<bool> TryClaimCheckInAsync(
        Guid ticketId,
        Guid expectedEventId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    public void Add(Ticket ticket)
    {
    }
}

internal sealed class NoopTicketUnitOfWorkFake : TicketFlow.Ticketing.Application.Abstractions.IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
