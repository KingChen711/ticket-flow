using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Reservation.Api.Consumers;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.SalePolicies;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

public sealed class CatalogSalePolicyHoldLifecycleTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_new_published_price_snapshot_does_not_invalidate_an_existing_immutable_quote()
    {
        var fixture = CreateFixture();
        await using var provider = fixture.Services.BuildServiceProvider(validateScopes: true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Message(
            fixture.EventId,
            fixture.SectionId,
            "Published",
            version: 12,
            fixture.Now));

        Assert.True(await harness.Consumed.Any<CatalogSalePolicyChanged>());
        Assert.Equal(HoldQuoteState.Active, fixture.Quote.State);
        Assert.Null(fixture.HoldStore.ReleaseRequest);
        Assert.Empty(fixture.Publisher.Published);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_cancelled_sale_policy_releases_and_invalidates_every_active_quote()
    {
        var fixture = CreateFixture();
        await using var provider = fixture.Services.BuildServiceProvider(validateScopes: true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Message(
            fixture.EventId,
            fixture.SectionId,
            "Cancelled",
            version: 12,
            fixture.Now));

        Assert.True(await harness.Consumed.Any<CatalogSalePolicyChanged>());
        Assert.Equal(HoldQuoteState.Invalidated, fixture.Quote.State);
        Assert.Equal(fixture.Quote.Id, fixture.HoldStore.ReleaseRequest?.HoldId);
        var notification = Assert.Single(fixture.Publisher.Published);
        Assert.Equal("Available", notification.Status);
        Assert.Equal(fixture.Quote.Generation, notification.Generation);
    }

    private static SalePolicyFixture CreateFixture()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var quote = HoldQuote.Create(
            Guid.NewGuid(),
            eventId,
            ownerId,
            [Guid.NewGuid()],
            125_000m,
            "VND",
            11,
            8,
            now,
            now.AddMinutes(10));
        var policy = EventSalePolicy.FromSnapshot(
            eventId,
            "Published",
            now.AddHours(-1),
            now.AddHours(1),
            11,
            now,
            [(sectionId, 125_000m, "VND")]);
        var holdStore = new RollbackAwareHoldStore();
        _ = holdStore.TryHoldAsync(new HoldStoreRequest(
            eventId,
            quote.SeatIds,
            quote.Id,
            quote.OwnerUserId,
            quote.Generation,
            quote.ExpiresAt - now)).GetAwaiter().GetResult();
        var publisher = new RecordingSeatAvailabilityEventPublisher();
        var services = new ServiceCollection()
            .AddSingleton<IEventSalePolicyRepository>(new FakeEventSalePolicyRepository(policy))
            .AddSingleton<IHoldQuoteRepository>(new FakeHoldQuoteRepository([quote]))
            .AddSingleton<ISeatHoldStore>(holdStore)
            .AddSingleton<IReservationOperationReceiptRepository>(new FakeReservationOperationReceiptRepository())
            .AddSingleton<ISeatAvailabilityEventPublisher>(publisher)
            .AddSingleton<IUnitOfWork>(new ControlledReservationUnitOfWork())
            .AddSingleton<TimeProvider>(new FixedTimeProvider(now))
            .AddMassTransitTestHarness(configuration =>
                configuration.AddConsumer<CatalogSalePolicyChangedConsumer>());

        return new SalePolicyFixture(services, eventId, sectionId, now, quote, holdStore, publisher);
    }

    private static CatalogSalePolicyChanged Message(
        Guid eventId,
        Guid sectionId,
        string status,
        long version,
        DateTimeOffset now) => new(
        eventId,
        status,
        now.AddHours(-1),
        now.AddHours(1),
        [new PriceTierSnapshotV2(sectionId, 150_000m, "VND")],
        version,
        now.AddMinutes(1));

    private sealed record SalePolicyFixture(
        IServiceCollection Services,
        Guid EventId,
        Guid SectionId,
        DateTimeOffset Now,
        HoldQuote Quote,
        RollbackAwareHoldStore HoldStore,
        RecordingSeatAvailabilityEventPublisher Publisher);
}
