using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TicketFlow.Catalog.Infrastructure;
using TicketFlow.Payment.Infrastructure;
using TicketFlow.Reservation.Infrastructure;
using TicketFlow.Ticketing.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Infrastructure;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class Phase12MigrationCompatibilityTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait("Category", HardeningTraits.Harness)]
    public async Task Empty_database_migrates_to_the_phase12_head()
    {
        var databaseName = $"phase12_blank_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);
        try
        {
            await using var reservation = CreateReservationContext(connectionString);
            await using var payment = CreatePaymentContext(connectionString);
            await using var ticketing = CreateTicketingContext(connectionString);
            await using var catalog = CreateCatalogContext(connectionString);

            await reservation.Database.MigrateAsync();
            await payment.Database.MigrateAsync();
            await ticketing.Database.MigrateAsync();
            await catalog.Database.MigrateAsync();

            Assert.Empty(await reservation.Database.GetPendingMigrationsAsync());
            Assert.Empty(await payment.Database.GetPendingMigrationsAsync());
            Assert.Empty(await ticketing.Database.GetPendingMigrationsAsync());
            Assert.Empty(await catalog.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
    }

    [Fact]
    [Trait("Category", HardeningTraits.Harness)]
    public async Task Phase11_schema_upgrades_expand_only_and_old_columns_remain_readable()
    {
        var databaseName = $"phase12_migration_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);
        try
        {
            await using var reservation = CreateReservationContext(connectionString);
            await using var payment = CreatePaymentContext(connectionString);
            await using var ticketing = CreateTicketingContext(connectionString);
            await using var catalog = CreateCatalogContext(connectionString);

            await MigrateToAsync(reservation, "20260713113512_InitialReservation");
            await MigrateToAsync(payment, "20260715015511_PaymentInboxOutbox");
            await MigrateToAsync(ticketing, "20260717020639_AddTicketCheckIn");
            await MigrateToAsync(catalog, "20260714011437_CatalogOutbox");

            await reservation.Database.MigrateAsync();
            await payment.Database.MigrateAsync();
            await ticketing.Database.MigrateAsync();
            await catalog.Database.MigrateAsync();

            Assert.Empty(await reservation.Database.GetPendingMigrationsAsync());
            Assert.Empty(await payment.Database.GetPendingMigrationsAsync());
            Assert.Empty(await ticketing.Database.GetPendingMigrationsAsync());
            Assert.Empty(await catalog.Database.GetPendingMigrationsAsync());

            await AssertOldColumnsRemainReadableAsync(connectionString);
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
    }

    private static ReservationDbContext CreateReservationContext(string connectionString) => new(
        new DbContextOptionsBuilder<ReservationDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private static PaymentDbContext CreatePaymentContext(string connectionString) => new(
        new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private static TicketingDbContext CreateTicketingContext(string connectionString) => new(
        new DbContextOptionsBuilder<TicketingDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private static CatalogDbContext CreateCatalogContext(string connectionString) => new(
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private static Task MigrateToAsync(DbContext context, string migrationId) =>
        context.Database.GetService<IMigrator>().MigrateAsync(migrationId);

    private static async Task AssertOldColumnsRemainReadableAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var query in new[]
                 {
                     "SELECT id, event_id, section_id, label, status FROM reservation.seats LIMIT 0",
                     "SELECT id, order_id, amount, currency, status, gateway_ref FROM payment.payment_transactions LIMIT 0",
                     "SELECT id, order_id, seat_id, qr_payload, pdf_object_key, issued_at, checked_in_at FROM ticketing.tickets LIMIT 0",
                     "SELECT id, title, description, artist, venue_id, schedule_starts_at, schedule_ends_at, status FROM catalog.events LIMIT 0",
                 })
        {
            await using var command = new NpgsqlCommand(query, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
