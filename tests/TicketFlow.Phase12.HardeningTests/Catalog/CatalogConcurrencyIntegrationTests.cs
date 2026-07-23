using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Events;
using TicketFlow.Catalog.Domain.SeatMaps;
using TicketFlow.Catalog.Infrastructure;
using TicketFlow.Phase12.HardeningTests.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Catalog;

/// <summary>
/// Proves Commit 11's two DB-level guards against real Postgres: the <c>xmin</c> concurrency
/// token that turns a lost race into a conflict instead of last-write-wins, and the unique
/// indexes that catch a duplicate section/price-tier race the in-memory domain guard alone
/// cannot, because it only sees one request's not-yet-saved state.
/// </summary>
[Collection(HardeningEnvironmentCollection.Name)]
public sealed class CatalogConcurrencyIntegrationTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Two_concurrent_saves_of_one_event_must_leave_exactly_one_winner()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var eventId = await SeedDraftEventAsync(connectionString);

        // Two independent contexts load the same row, both decide to publish, both save — the
        // second SaveChangesAsync must see its xmin no longer match and lose, not silently
        // overwrite the first.
        await using var first = CreateContext(connectionString);
        await using var second = CreateContext(connectionString);
        var firstEvent = await first.Events.SingleAsync(e => e.Id == eventId);
        var secondEvent = await second.Events.SingleAsync(e => e.Id == eventId);

        firstEvent.Publish(DateTimeOffset.UtcNow);
        secondEvent.Cancel();

        await first.SaveChangesAsync();
        var error = await Record.ExceptionAsync(() => second.SaveChangesAsync());

        Assert.IsType<DbUpdateConcurrencyException>(error);

        await using var verify = CreateContext(connectionString);
        var stored = await verify.Events.SingleAsync(e => e.Id == eventId);
        Assert.Equal(EventStatus.Published, stored.Status);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_AddSection_with_the_same_name_must_leave_exactly_one_section()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var eventId = await SeedDraftEventAsync(connectionString);
        var seatMapId = await SeedSeatMapAsync(connectionString, eventId);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = CreateContext(connectionString);
            var seatMap = await context.SeatMaps.SingleAsync(s => s.Id == seatMapId);
            try
            {
                seatMap.AddSection("Floor A", capacity: 100);
                await context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
        }));

        // Every context loaded the seat map before any of them had saved, so the domain's own
        // OrdinalIgnoreCase duplicate check (in-memory, per request) could not see the others —
        // the unique index is what actually leaves exactly one section standing.
        Assert.Equal(1, attempts.Count(succeeded => succeeded));

        await using var verify = CreateContext(connectionString);
        var stored = await verify.SeatMaps.SingleAsync(s => s.Id == seatMapId);
        Assert.Single(stored.Sections);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_section_name_differing_only_by_case_must_still_be_rejected_by_the_database()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var eventId = await SeedDraftEventAsync(connectionString);
        var seatMapId = await SeedSeatMapAsync(connectionString, eventId);

        // BOTH contexts load before either saves — that is the whole point. Loading the second one
        // afterwards would just re-run the domain's own OrdinalIgnoreCase guard against an already
        // persisted section, which proves nothing about the database. Here each in-memory aggregate
        // knows only about its own section, both pass that guard, and the case-insensitive
        // functional index (raw SQL — EF's fluent API has no functional-index support) is the only
        // thing left that can reject the duplicate.
        await using var first = CreateContext(connectionString);
        await using var second = CreateContext(connectionString);
        var firstMap = await first.SeatMaps.SingleAsync(s => s.Id == seatMapId);
        var secondMap = await second.SeatMaps.SingleAsync(s => s.Id == seatMapId);

        firstMap.AddSection("Floor A", 100);
        secondMap.AddSection("FLOOR A", 50);

        await first.SaveChangesAsync();
        var error = await Record.ExceptionAsync(() => second.SaveChangesAsync());

        Assert.IsType<DbUpdateException>(error);
    }

    private async Task<Guid> SeedDraftEventAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        var schedule = EventSchedule.Create(DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(30).AddHours(3));
        var @event = Event.Create("Concert", "Description", "Artist", Guid.NewGuid(), schedule);
        @event.AddPriceTier(Guid.NewGuid(), Money.Create(500_000m, "VND"));

        context.Events.Add(@event);
        await context.SaveChangesAsync();
        return @event.Id;
    }

    private async Task<Guid> SeedSeatMapAsync(string connectionString, Guid eventId)
    {
        await using var context = CreateContext(connectionString);
        var seatMap = SeatMap.CreateForEvent(eventId);
        context.SeatMaps.Add(seatMap);
        await context.SaveChangesAsync();
        return seatMap.Id;
    }

    private async Task<string> CreateIsolatedDatabaseAsync()
    {
        var databaseName = $"catalog_hardening_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString) { Database = "postgres" };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();

        var testBuilder = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString) { Database = databaseName };
        var testConnectionString = testBuilder.ConnectionString;

        await using (var schemaContext = CreateContext(testConnectionString))
            await schemaContext.Database.EnsureCreatedAsync();

        // EnsureCreatedAsync builds tables/plain indexes from the EF model but does not run raw
        // SQL migration steps — the case-insensitive functional index only exists via the
        // migration's Sql(...) call, so it has to be created here too for this test to be real.
        await using (var connection2 = new NpgsqlConnection(testConnectionString))
        {
            await connection2.OpenAsync();
            await using var indexCommand = connection2.CreateCommand();
            indexCommand.CommandText =
                "create unique index ix_seat_map_sections_seat_map_id_name_ci " +
                "on catalog.seat_map_sections (seat_map_id, lower(name));";
            await indexCommand.ExecuteNonQueryAsync();
        }

        return testConnectionString;
    }

    private static CatalogDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CatalogDbContext(options);
    }
}
