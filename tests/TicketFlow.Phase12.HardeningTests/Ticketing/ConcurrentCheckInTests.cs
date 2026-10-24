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
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Two_concurrent_scans_must_return_exactly_one_Valid()
    {
        var eventId = Guid.NewGuid();
        var (connectionString, ticket) = await SeedTicketAsync(eventId);

        const int scannerCount = 2;
        var readBarrier = new AsyncRendezvous(scannerCount);
        var results = await Task.WhenAll(Enumerable.Range(0, scannerCount)
            .Select(_ => ScanLikeProductionEndpointAsync(connectionString, ticket.QrPayload, eventId, readBarrier)));

        // The barrier guarantees both scanners read CheckedInAt == null before either writes, so
        // the only thing that can separate them is the conditional UPDATE itself.
        Assert.Equal(1, results.Count(result => result == "Valid"));
        Assert.Equal(scannerCount - 1, results.Count(result => result == "AlreadyCheckedIn"));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_hundred_concurrent_scans_must_still_admit_exactly_one()
    {
        var eventId = Guid.NewGuid();
        var (connectionString, ticket) = await SeedTicketAsync(eventId);

        const int scannerCount = 100;
        var readBarrier = new AsyncRendezvous(scannerCount);
        var results = await Task.WhenAll(Enumerable.Range(0, scannerCount)
            .Select(_ => ScanLikeProductionEndpointAsync(connectionString, ticket.QrPayload, eventId, readBarrier)));

        Assert.Equal(1, results.Count(result => result == "Valid"));
        Assert.Equal(scannerCount - 1, results.Count(result => result == "AlreadyCheckedIn"));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_ticket_for_another_event_must_be_refused_not_admitted()
    {
        var ticketEventId = Guid.NewGuid();
        var (connectionString, ticket) = await SeedTicketAsync(ticketEventId);

        // A scanner at the wrong door: the ticket is genuine, just not for tonight.
        var result = await ScanLikeProductionEndpointAsync(
            connectionString, ticket.QrPayload, Guid.NewGuid(), new AsyncRendezvous(1));

        Assert.Equal("WrongEvent", result);

        await using var context = CreateContext(connectionString);
        var stored = await context.Tickets.SingleAsync(t => t.Id == ticket.Id);
        Assert.Null(stored.CheckedInAt);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task An_eventless_legacy_ticket_must_be_refused_until_reconciled()
    {
        var (connectionString, ticket) = await SeedTicketAsync(eventId: null);

        // There is no durable evidence that this old ticket belongs at the selected event. Treat
        // it as a migration/reconciliation case, never as a pass for every gate an organizer owns.
        var result = await ScanLikeProductionEndpointAsync(
            connectionString, ticket.QrPayload, Guid.NewGuid(), new AsyncRendezvous(1));

        Assert.Equal("WrongEvent", result);

        await using var context = CreateContext(connectionString);
        Assert.Null((await context.Tickets.SingleAsync(t => t.Id == ticket.Id)).CheckedInAt);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_revoked_ticket_must_never_be_admitted()
    {
        var eventId = Guid.NewGuid();
        var (connectionString, ticket) = await SeedTicketAsync(eventId, revoke: true);

        var result = await ScanLikeProductionEndpointAsync(
            connectionString, ticket.QrPayload, eventId, new AsyncRendezvous(1));

        // Refunded, but the PDF still exists and can still be presented at the gate. "Revoked"
        // rather than "NotFound" so staff can tell a refund apart from a forgery.
        Assert.Equal("Revoked", result);

        await using var context = CreateContext(connectionString);
        var stored = await context.Tickets.SingleAsync(t => t.Id == ticket.Id);
        Assert.Null(stored.CheckedInAt);
    }

    private async Task<(string ConnectionString, Ticket Ticket)> SeedTicketAsync(Guid? eventId, bool revoke = false)
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        await using (var schemaContext = CreateContext(connectionString))
            await schemaContext.Database.EnsureCreatedAsync();

        var ticket = Ticket.Issue(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"qr-{Guid.NewGuid():N}",
            $"ticket-{Guid.NewGuid():N}.pdf",
            DateTimeOffset.UtcNow,
            customerId: Guid.NewGuid(),
            eventId: eventId);

        if (revoke)
            ticket.Revoke("Order refunded.", DateTimeOffset.UtcNow);

        await using (var seedContext = CreateContext(connectionString))
        {
            seedContext.Tickets.Add(ticket);
            await seedContext.SaveChangesAsync();
        }

        return (connectionString, ticket);
    }

    /// <summary>
    /// Mirrors the production endpoint's decision path exactly: evaluate for a specific message,
    /// then let the conditional UPDATE decide, then re-read to explain a lost race.
    /// </summary>
    private static async Task<string> ScanLikeProductionEndpointAsync(
        string connectionString,
        string qrPayload,
        Guid gateEventId,
        AsyncRendezvous readBarrier)
    {
        await using var context = CreateContext(connectionString);
        var repository = new TicketRepository(context);
        var ticket = await context.Tickets.AsNoTracking().SingleAsync(item => item.QrPayload == qrPayload);

        // Deterministically opens the race after every scanner has observed CheckedInAt == null.
        await readBarrier.SignalAndWaitAsync();

        var evaluation = ticket.EvaluateScan(gateEventId);
        if (evaluation.Outcome != CheckInOutcome.Valid)
            return evaluation.Outcome.ToString();

        if (await repository.TryClaimCheckInAsync(ticket.Id, gateEventId, DateTimeOffset.UtcNow))
            return nameof(CheckInOutcome.Valid);

        var loser = await context.Tickets.AsNoTracking().SingleAsync(item => item.Id == ticket.Id);
        var reason = loser.EvaluateScan(gateEventId);
        return reason.Outcome == CheckInOutcome.Valid
            ? nameof(CheckInOutcome.AlreadyCheckedIn)
            : reason.Outcome.ToString();
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
