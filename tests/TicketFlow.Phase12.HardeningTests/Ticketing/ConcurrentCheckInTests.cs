using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketFlow.Phase12.HardeningTests.Infrastructure;
using TicketFlow.Phase12.HardeningTests.Support;
using TicketFlow.Ticketing.Domain.Tickets;
using TicketFlow.Ticketing.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Ticketing;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class ConcurrentCheckInTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Two_concurrent_scans_must_return_exactly_one_Valid()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        await using (var schemaContext = CreateContext(connectionString))
            await schemaContext.Database.EnsureCreatedAsync();

        var ticket = Ticket.Issue(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"qr-{Guid.NewGuid():N}",
            $"ticket-{Guid.NewGuid():N}.pdf",
            DateTimeOffset.UtcNow);

        await using (var seedContext = CreateContext(connectionString))
        {
            seedContext.Tickets.Add(ticket);
            await seedContext.SaveChangesAsync();
        }

        const int scannerCount = 2;
        var readBarrier = new AsyncRendezvous(scannerCount);
        var scans = Enumerable.Range(0, scannerCount)
            .Select(_ => ScanLikeProductionEndpointAsync(connectionString, ticket.QrPayload, readBarrier))
            .ToArray();

        var results = await Task.WhenAll(scans);

        // Current endpoint does read -> domain mutation -> SaveChanges with no conditional
        // update/concurrency token. Every context reads NULL before the barrier and returns Valid.
        Assert.Equal(1, results.Count(result => result == "Valid"));
        Assert.Equal(scannerCount - 1, results.Count(result => result == "AlreadyCheckedIn"));
    }

    private async Task<string> ScanLikeProductionEndpointAsync(
        string connectionString,
        string qrPayload,
        AsyncRendezvous readBarrier)
    {
        await using var context = CreateContext(connectionString);
        var ticket = await context.Tickets.SingleAsync(item => item.QrPayload == qrPayload);

        // Deterministically opens the race after all scanners have observed CheckedInAt == null.
        await readBarrier.SignalAndWaitAsync();

        if (ticket.CheckedInAt is not null)
            return "AlreadyCheckedIn";

        ticket.CheckIn(DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();
        return "Valid";
    }

    private async Task<string> CreateIsolatedDatabaseAsync()
    {
        var databaseName = $"ticketing_hardening_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = "postgres",
        };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();

        var testBuilder = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        };
        return testBuilder.ConnectionString;
    }

    private static TicketingDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TicketingDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new TicketingDbContext(options);
    }
}
