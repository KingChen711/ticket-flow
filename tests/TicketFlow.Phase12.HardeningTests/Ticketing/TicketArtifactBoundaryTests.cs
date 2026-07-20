using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Phase12.HardeningTests.Support;
using TicketFlow.Ticketing.Api.Consumers;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Phase12.HardeningTests.Ticketing;

public sealed class TicketArtifactBoundaryTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Lost_response_after_PDF_upload_must_not_upload_the_same_ticket_twice_on_retry()
    {
        var sideEffect = new ControlledSideEffect<string>(
            () => "deterministic/object.pdf",
            ControlledSideEffectOutcome.SideEffectSucceededButResponseLost);

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITicketRepository, EmptyTicketRepositoryFake>()
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

        await harness.Bus.Publish(command, context => context.MessageId = Guid.NewGuid());
        Assert.True(await sideEffect.WaitForSuccessfulSideEffectsAsync(1, TimeSpan.FromSeconds(10)));
        Assert.True(await harness.Published.Any<TicketingFailed>(x => x.Context.Message.OrderId == orderId));

        await harness.Bus.Publish(command, context => context.MessageId = Guid.NewGuid());
        Assert.True(await sideEffect.WaitForSuccessfulSideEffectsAsync(2, TimeSpan.FromSeconds(10)));

        // A deterministic object key alone does not prove the external write is idempotent or
        // recover its receipt. Current consumer invokes the renderer again after ambiguity.
        Assert.Equal(1, sideEffect.SuccessfulSideEffects);
    }
}

internal sealed class EmptyTicketRepositoryFake : ITicketRepository
{
    public Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Ticket>>([]);

    public Task<Ticket?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<Ticket?>(null);

    public Task<Ticket?> GetByQrPayloadAsync(string qrPayload, CancellationToken cancellationToken = default) =>
        Task.FromResult<Ticket?>(null);

    public void Add(Ticket ticket)
    {
    }
}

internal sealed class NoopTicketUnitOfWorkFake : TicketFlow.Ticketing.Application.Abstractions.IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
