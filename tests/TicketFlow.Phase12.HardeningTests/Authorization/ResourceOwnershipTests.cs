using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Application.Events;
using TicketFlow.Catalog.Domain.Events;
using TicketFlow.Catalog.Domain.Venues;
using TicketFlow.Catalog.Infrastructure;
using TicketFlow.Catalog.Infrastructure.Repositories;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Phase12.HardeningTests.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Authorization;

/// <summary>
/// The authorization matrix Commit 14 exists to establish: organizer A, organizer B, admin, and a
/// legacy event that belongs to nobody, against every Catalog mutation.
/// </summary>
/// <remarks>
/// Before this commit all of these passed for anyone holding the organizer role, because
/// <c>RequireOrganizer</c> only ever answered "is this caller AN organizer" and never "is this
/// caller THIS event's organizer". Cancel is the one that matters most — it closes sale and voids
/// every unconfirmed hold immediately (Commit 3).
/// </remarks>
[Collection(HardeningEnvironmentCollection.Name)]
public sealed class ResourceOwnershipTests(HardeningEnvironmentFixture environment)
{
    private static readonly Guid OrganizerA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OrganizerB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static CatalogCaller AsOrganizer(Guid id) => new(id, IsAdmin: false);
    private static CatalogCaller AsAdmin() => new(Guid.Parse("dddddddd-0000-0000-0000-000000000004"), IsAdmin: true);

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task An_event_is_stamped_with_the_organizer_from_the_token_not_the_request()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var eventId = await SeedEventAsync(context, OrganizerA);

        var stored = await context.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        Assert.True(stored.IsOwnedBy(OrganizerA));
        Assert.False(stored.IsOwnedBy(OrganizerB));
    }

    [Theory]
    [InlineData("reschedule")]
    [InlineData("publish")]
    [InlineData("price-tier")]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Another_organizer_cannot_mutate_an_event_they_do_not_own(string mutation)
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var eventId = await SeedEventAsync(context, OrganizerA);
        var service = CreateService(context);

        var error = await Record.ExceptionAsync(() => Mutate(service, mutation, eventId, AsOrganizer(OrganizerB)));

        Assert.IsType<EventOwnershipException>(error);
    }

    [Theory]
    [InlineData("reschedule")]
    [InlineData("publish")]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task The_owning_organizer_and_an_admin_can_both_mutate(string mutation)
    {
        var connectionString = await CreateIsolatedDatabaseAsync();

        await using (var ownerContext = CreateContext(connectionString))
        {
            var ownedEventId = await SeedEventAsync(ownerContext, OrganizerA);
            await Mutate(CreateService(ownerContext), mutation, ownedEventId, AsOrganizer(OrganizerA));
        }

        await using var adminContext = CreateContext(connectionString);
        var adminEventId = await SeedEventAsync(adminContext, OrganizerA);
        await Mutate(CreateService(adminContext), mutation, adminEventId, AsAdmin());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_legacy_event_with_no_owner_belongs_to_nobody_but_stays_reachable_by_an_admin()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        await using var context = CreateContext(connectionString);
        // OrganizerId is nullable because Commit 2 created the column expand-only; every event
        // that existed before this commit looks exactly like this one.
        var eventId = await SeedEventAsync(context, organizerId: null);
        var service = CreateService(context);

        // Unowned must not mean "self-service": the first organizer to guess the id would
        // otherwise inherit somebody else's event.
        var claimAttempt = await Record.ExceptionAsync(
            () => service.PublishAsync(eventId, AsOrganizer(OrganizerA)));
        Assert.IsType<EventOwnershipException>(claimAttempt);

        // ...but it must not become permanently unmanageable either.
        await service.PublishAsync(eventId, AsAdmin());
        var stored = await context.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        Assert.Equal(EventStatus.Published, stored.Status);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Publishing_broadcasts_the_owner_so_other_services_can_scope_without_asking_catalog()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var eventId = await SeedEventAsync(context, OrganizerA);
        var publisher = new RecordingIntegrationEventPublisher();

        await CreateService(context, publisher).PublishAsync(eventId, AsOrganizer(OrganizerA));

        var published = Assert.Single(publisher.Published.OfType<CatalogEventPublished>());
        Assert.Equal(OrganizerA, published.OrganizerId);
    }

    private static Task Mutate(EventService service, string mutation, Guid eventId, CatalogCaller caller) =>
        mutation switch
        {
            "reschedule" => service.RescheduleAsync(
                eventId,
                new RescheduleEventRequest(DateTimeOffset.UtcNow.AddDays(60), DateTimeOffset.UtcNow.AddDays(60).AddHours(3)),
                caller),
            "publish" => service.PublishAsync(eventId, caller),
            "price-tier" => service.AddPriceTierAsync(
                eventId, new AddPriceTierRequest(Guid.NewGuid(), 100_000m, "VND"), caller),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation."),
        };

    private static EventService CreateService(
        CatalogDbContext context,
        RecordingIntegrationEventPublisher? publisher = null) =>
        new(new EventRepository(context),
            new VenueRepository(context),
            new SeatMapRepository(context),
            new UnitOfWork(context),
            publisher ?? new RecordingIntegrationEventPublisher(),
            TimeProvider.System);

    private static async Task<Guid> SeedEventAsync(CatalogDbContext context, Guid? organizerId)
    {
        var venue = Venue.Register("Arena", Address.Create("1 Main", "Ho Chi Minh City", "VN", "70000"));
        context.Venues.Add(venue);

        var schedule = EventSchedule.Create(DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(30).AddHours(3));
        var @event = Event.Create("Concert", "Description", "Artist", venue.Id, schedule, organizerId);
        // Publish refuses an event with no price tier. Seeded so the publish cases fail on
        // ownership or succeed — never on an unrelated domain invariant. That the deny cases pass
        // WITHOUT this is itself informative: ownership is checked before the aggregate is touched.
        @event.AddPriceTier(Guid.NewGuid(), Money.Create(500_000m, "VND"));
        context.Events.Add(@event);
        await context.SaveChangesAsync();
        return @event.Id;
    }

    private async Task<string> CreateIsolatedDatabaseAsync()
    {
        var databaseName = $"authz_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString) { Database = "postgres" };

        await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testConnectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await using (var schemaContext = CreateContext(testConnectionString))
            await schemaContext.Database.EnsureCreatedAsync();

        return testConnectionString;
    }

    private static CatalogDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    /// <summary>Captures what Catalog would have put on the bus, without needing one.</summary>
    private sealed class RecordingIntegrationEventPublisher : IIntegrationEventPublisher
    {
        public List<object> Published { get; } = [];

        public Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default)
            where T : class
        {
            Published.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }
}
