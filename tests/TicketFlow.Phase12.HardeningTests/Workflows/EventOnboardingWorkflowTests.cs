using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Application.Workflows;
using TicketFlow.Catalog.Domain.SeatMaps;
using TicketFlow.Catalog.Domain.Venues;
using TicketFlow.Catalog.Domain.Workflows;
using CatalogEvent = TicketFlow.Catalog.Domain.Events.Event;

namespace TicketFlow.Phase12.HardeningTests.Workflows;

public sealed class EventOnboardingWorkflowTests
{
    /// <summary>
    /// Commit 14 gave onboarding a durable owner, so every start needs a caller. A fixed organizer
    /// keeps these tests about workflow resume/compensation, which is what they exist to prove.
    /// </summary>
    private static readonly CatalogCaller Organizer =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), IsAdmin: false);

    [Theory]
    [InlineData(EventOnboardingStep.Venue)]
    [InlineData(EventOnboardingStep.Event)]
    [InlineData(EventOnboardingStep.SeatMap)]
    [InlineData(EventOnboardingStep.SectionsAndPricing)]
    [InlineData(EventOnboardingStep.ReservationInventory)]
    [InlineData(EventOnboardingStep.Published)]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Every_step_can_fail_then_resume_without_duplicate_resources(EventOnboardingStep failAt)
    {
        var fixture = new Fixture(failAt);
        var request = Request(Guid.NewGuid().ToString("N"));

        var failed = await fixture.Service.StartAsync(request, Organizer);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(failAt.ToString(), failed.FailedStep);

        var completed = await fixture.Service.ResumeAsync(failed.WorkflowId, Organizer);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("Published", completed.LastCompletedStep);

        var replay = await fixture.Service.StartAsync(request, Organizer);
        Assert.Equal(completed.WorkflowId, replay.WorkflowId);
        Assert.Equal(completed.EventId, replay.EventId);
        Assert.Single(fixture.Venues.Items);
        Assert.Single(fixture.Events.Items);
        Assert.Single(fixture.SeatMaps.Items);
        Assert.Single(fixture.SeatMaps.Items.Single().Sections);
        Assert.Single(fixture.Events.Items.Single().PriceTiers);
        Assert.Equal(1, fixture.Reservation.ProvisionedEvents.Count);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Same_creation_key_cannot_be_rebound_to_different_input()
    {
        var fixture = new Fixture(null);
        var request = Request("stable-key");
        var completed = await fixture.Service.StartAsync(request, Organizer);
        Assert.Equal("Completed", completed.Status);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.StartAsync(request with { Title = "A different event" }, Organizer));
        Assert.Contains("different request", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Events.Items);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Another_organizer_cannot_read_resume_adopt_or_compensate_the_workflow()
    {
        var fixture = new Fixture(EventOnboardingStep.Published);
        var request = Request("owner-scoped-workflow");
        var failed = await fixture.Service.StartAsync(request, Organizer);
        var stranger = new CatalogCaller(Guid.NewGuid(), IsAdmin: false);

        await Assert.ThrowsAsync<WorkflowOwnershipException>(() =>
            fixture.Service.GetAsync(failed.WorkflowId, stranger));
        await Assert.ThrowsAsync<WorkflowOwnershipException>(() =>
            fixture.Service.GetByCreationKeyAsync(request.CreationKey, stranger));
        await Assert.ThrowsAsync<WorkflowOwnershipException>(() =>
            fixture.Service.ResumeAsync(failed.WorkflowId, stranger));
        await Assert.ThrowsAsync<WorkflowOwnershipException>(() =>
            fixture.Service.CompensateAsync(failed.WorkflowId, stranger));
        await Assert.ThrowsAsync<WorkflowOwnershipException>(() =>
            fixture.Service.StartAsync(request, stranger));

        Assert.Single(fixture.Events.Items);
        Assert.Single(fixture.SeatMaps.Items);
        Assert.Single(fixture.Venues.Items);

        var admin = new CatalogCaller(Guid.NewGuid(), IsAdmin: true);
        Assert.NotNull(await fixture.Service.GetAsync(failed.WorkflowId, admin));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Failed_draft_can_be_explicitly_compensated_including_uncertain_inventory()
    {
        var fixture = new Fixture(EventOnboardingStep.Published);
        var failed = await fixture.Service.StartAsync(Request("cleanup-key"), Organizer);
        Assert.Equal("ReservationInventory", failed.LastCompletedStep);
        Assert.Single(fixture.Reservation.ProvisionedEvents);

        var compensated = await fixture.Service.CompensateAsync(failed.WorkflowId, Organizer);
        Assert.Equal("Compensated", compensated.Status);
        Assert.Empty(fixture.Events.Items);
        Assert.Empty(fixture.SeatMaps.Items);
        Assert.Empty(fixture.Venues.Items);
        Assert.Empty(fixture.Reservation.ProvisionedEvents);

        var replay = await fixture.Service.CompensateAsync(failed.WorkflowId, Organizer);
        Assert.Equal("Compensated", replay.Status);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Creation_key_unique_race_reloads_the_durable_winner_and_continues()
    {
        var workflows = new InMemoryOnboardingWorkflowRepository();
        var venues = new InMemoryVenueRepository();
        var events = new InMemoryEventRepository();
        var seatMaps = new InMemorySeatMapRepository();
        var reservation = new InMemoryReservationInventoryClient();
        var service = new EventOnboardingWorkflowService(
            workflows,
            venues,
            events,
            seatMaps,
            reservation,
            new RecordingCatalogPublisher(),
            new FailOnceInjector(null),
            new ThrowFirstConcurrencyUnitOfWork(),
            TimeProvider.System);

        var completed = await service.StartAsync(Request("racing-creation-key"), Organizer);

        Assert.Equal("Completed", completed.Status);
        Assert.Single(events.Items);
        Assert.Equal(completed.EventId, events.Items.Single().Id);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Lost_inventory_response_resumes_same_event_and_provisions_once()
    {
        var workflows = new InMemoryOnboardingWorkflowRepository();
        var venues = new InMemoryVenueRepository();
        var events = new InMemoryEventRepository();
        var seatMaps = new InMemorySeatMapRepository();
        var reservation = new LostFirstProvisionResponseClient();
        var service = new EventOnboardingWorkflowService(
            workflows,
            venues,
            events,
            seatMaps,
            reservation,
            new RecordingCatalogPublisher(),
            new FailOnceInjector(null),
            new InMemoryCatalogUnitOfWork(),
            TimeProvider.System);

        var failed = await service.StartAsync(Request("lost-inventory-response"), Organizer);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal("ReservationInventory", failed.FailedStep);

        var completed = await service.ResumeAsync(failed.WorkflowId, Organizer);
        Assert.Equal("Completed", completed.Status);
        Assert.Single(reservation.ProvisionedEvents);
        Assert.Equal(2, reservation.Calls);
        Assert.Single(events.Items);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Compensation_refuses_published_event_before_deleting_reservation_inventory()
    {
        var fixture = new Fixture(EventOnboardingStep.Published);
        var failed = await fixture.Service.StartAsync(Request("publish-compensation-race"), Organizer);
        var @event = Assert.Single(fixture.Events.Items);
        @event.Publish(DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CompensateAsync(failed.WorkflowId, Organizer));

        Assert.Contains(failed.EventId, fixture.Reservation.ProvisionedEvents);
        Assert.Single(fixture.Events.Items);
    }

    private static StartEventOnboardingRequest Request(string creationKey) => new(
        creationKey,
        null,
        new NewVenueDefinition("Durable Hall", "1 Workflow Way", "Saigon", "VN", null),
        "Resumable Show",
        "A durable onboarding test",
        "Workflow Artist",
        DateTimeOffset.UtcNow.AddDays(30),
        DateTimeOffset.UtcNow.AddDays(30).AddHours(3),
        [new EventOnboardingSectionRequest("General", 50, 450_000m, "VND")]);

    private sealed class Fixture
    {
        public InMemoryVenueRepository Venues { get; } = new();
        public InMemoryEventRepository Events { get; } = new();
        public InMemorySeatMapRepository SeatMaps { get; } = new();
        public InMemoryReservationInventoryClient Reservation { get; } = new();
        public EventOnboardingWorkflowService Service { get; }

        public Fixture(EventOnboardingStep? failAt)
        {
            Service = new EventOnboardingWorkflowService(
                new InMemoryOnboardingWorkflowRepository(),
                Venues,
                Events,
                SeatMaps,
                Reservation,
                new RecordingCatalogPublisher(),
                new FailOnceInjector(failAt),
                new InMemoryCatalogUnitOfWork(),
                TimeProvider.System);
        }
    }

    private sealed class InMemoryOnboardingWorkflowRepository : IEventOnboardingWorkflowRepository
    {
        private readonly Dictionary<Guid, EventOnboardingWorkflow> _byId = [];
        private readonly Dictionary<string, EventOnboardingWorkflow> _byKey = new(StringComparer.Ordinal);

        public Task<EventOnboardingWorkflow?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_byId.GetValueOrDefault(id));

        public Task<EventOnboardingWorkflow?> GetByCreationKeyAsync(
            string creationKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_byKey.GetValueOrDefault(creationKey));

        public void Add(EventOnboardingWorkflow workflow)
        {
            _byId.Add(workflow.Id, workflow);
            _byKey.Add(workflow.CreationKey, workflow);
        }
    }

    private sealed class InMemoryVenueRepository : IVenueRepository
    {
        private readonly Dictionary<Guid, Venue> _items = [];
        public IReadOnlyCollection<Venue> Items => _items.Values;
        public Task<Venue?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));
        public Task<IReadOnlyList<Venue>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Venue>>(_items.Values.ToList());
        public void Add(Venue venue) => _items.Add(venue.Id, venue);
        public void Remove(Venue venue) => _items.Remove(venue.Id);
    }

    private sealed class InMemoryEventRepository : IEventRepository
    {
        private readonly Dictionary<Guid, CatalogEvent> _items = [];
        public IReadOnlyCollection<CatalogEvent> Items => _items.Values;
        public Task<CatalogEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));
        public Task<IReadOnlyList<CatalogEvent>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CatalogEvent>>(_items.Values.ToList());
        public void Add(CatalogEvent @event) => _items.Add(@event.Id, @event);
        public void Remove(CatalogEvent @event) => _items.Remove(@event.Id);
    }

    private sealed class InMemorySeatMapRepository : ISeatMapRepository
    {
        private readonly Dictionary<Guid, SeatMap> _items = [];
        public IReadOnlyCollection<SeatMap> Items => _items.Values;
        public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));
        public Task<SeatMap?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Values.SingleOrDefault(item => item.EventId == eventId));
        public void Add(SeatMap seatMap) => _items.Add(seatMap.Id, seatMap);
        public void Remove(SeatMap seatMap) => _items.Remove(seatMap.Id);
    }

    private sealed class InMemoryReservationInventoryClient : IReservationInventoryClient
    {
        public HashSet<Guid> ProvisionedEvents { get; } = [];
        public Task ProvisionAsync(
            Guid eventId,
            IReadOnlyList<ReservationInventorySection> sections,
            CancellationToken cancellationToken = default)
        {
            ProvisionedEvents.Add(eventId);
            return Task.CompletedTask;
        }

        public Task CleanupAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            ProvisionedEvents.Remove(eventId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCatalogPublisher : IIntegrationEventPublisher
    {
        public List<object> Messages { get; } = [];
        public Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default) where T : class
        {
            Messages.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnceInjector(EventOnboardingStep? failAt) : IWorkflowFailureInjector
    {
        private int _failed;
        public Task BeforeStepAsync(
            Guid workflowId,
            EventOnboardingStep step,
            CancellationToken cancellationToken = default)
        {
            if (step == failAt && Interlocked.Exchange(ref _failed, 1) == 0)
                throw new InvalidOperationException($"Injected failure at {step}.");
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryCatalogUnitOfWork : IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowFirstConcurrencyUnitOfWork : IUnitOfWork
    {
        private int _thrown;

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new ConcurrencyConflictException("Injected CreationKey race.");
            return Task.CompletedTask;
        }
    }

    private sealed class LostFirstProvisionResponseClient : IReservationInventoryClient
    {
        private int _lost;
        public HashSet<Guid> ProvisionedEvents { get; } = [];
        public int Calls { get; private set; }

        public Task ProvisionAsync(
            Guid eventId,
            IReadOnlyList<ReservationInventorySection> sections,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            ProvisionedEvents.Add(eventId);
            if (Interlocked.Exchange(ref _lost, 1) == 0)
                throw new IOException("Inventory committed but the HTTP response was lost.");
            return Task.CompletedTask;
        }

        public Task CleanupAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            ProvisionedEvents.Remove(eventId);
            return Task.CompletedTask;
        }
    }
}
