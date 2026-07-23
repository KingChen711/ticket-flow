using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TicketFlow.Catalog.Infrastructure;
using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Domain.Users;
using TicketFlow.Identity.Infrastructure;
using TicketFlow.Payment.Infrastructure;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Infrastructure;
using TicketFlow.Ticketing.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Infrastructure;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class Phase12MigrationCompatibilityTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait("Category", HardeningTraits.Harness)]
    public async Task Identity_allows_only_one_registration_owner_for_a_normalized_email()
    {
        var databaseName = $"phase12_identity_race_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);
        try
        {
            await using (var migrator = CreateIdentityContext(connectionString))
                await migrator.Database.MigrateAsync();

            await using var firstContext = CreateIdentityContext(connectionString);
            await using var secondContext = CreateIdentityContext(connectionString);
            var now = DateTimeOffset.UtcNow;
            firstContext.UserRegistrations.Add(UserRegistration.Start(
                Guid.NewGuid(), "squatting@example.com", "First", now));
            secondContext.UserRegistrations.Add(UserRegistration.Start(
                Guid.NewGuid(), "squatting@example.com", "Second", now));

            var outcomes = await Task.WhenAll(
                SaveRegistrationAsync(firstContext),
                SaveRegistrationAsync(secondContext));

            Assert.Equal(1, outcomes.Count(error => error is null));
            Assert.Single(outcomes, error => error is RegistrationPersistenceConflictException);

            await using var verifier = CreateIdentityContext(connectionString);
            Assert.Equal(1, await verifier.UserRegistrations.CountAsync(
                registration => registration.NormalizedEmail == "squatting@example.com"));
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
    }

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
            await using var identity = CreateIdentityContext(connectionString);

            await reservation.Database.MigrateAsync();
            await payment.Database.MigrateAsync();
            await ticketing.Database.MigrateAsync();
            await catalog.Database.MigrateAsync();
            await identity.Database.MigrateAsync();

            Assert.Empty(await reservation.Database.GetPendingMigrationsAsync());
            Assert.Empty(await payment.Database.GetPendingMigrationsAsync());
            Assert.Empty(await ticketing.Database.GetPendingMigrationsAsync());
            Assert.Empty(await catalog.Database.GetPendingMigrationsAsync());
            Assert.Empty(await identity.Database.GetPendingMigrationsAsync());
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
            await using var identity = CreateIdentityContext(connectionString);

            await MigrateToAsync(reservation, "20260713113512_InitialReservation");
            await MigrateToAsync(payment, "20260715015511_PaymentInboxOutbox");
            await MigrateToAsync(ticketing, "20260717020639_AddTicketCheckIn");
            await MigrateToAsync(catalog, "20260714011437_CatalogOutbox");
            await MigrateToAsync(identity, "20260716123449_DropPasswordHash");

            await reservation.Database.MigrateAsync();
            await payment.Database.MigrateAsync();
            await ticketing.Database.MigrateAsync();
            await catalog.Database.MigrateAsync();
            await identity.Database.MigrateAsync();

            Assert.Empty(await reservation.Database.GetPendingMigrationsAsync());
            Assert.Empty(await payment.Database.GetPendingMigrationsAsync());
            Assert.Empty(await ticketing.Database.GetPendingMigrationsAsync());
            Assert.Empty(await catalog.Database.GetPendingMigrationsAsync());
            Assert.Empty(await identity.Database.GetPendingMigrationsAsync());

            await AssertOldColumnsRemainReadableAsync(connectionString);
        }
        finally
        {
            await DropDatabaseAsync(environment.PostgresConnectionString, databaseName);
        }
    }

    [Fact]
    [Trait("Category", HardeningTraits.Harness)]
    public async Task Authoritative_checkout_rows_upgrade_to_the_durable_quote_and_receipt_lifecycle()
    {
        var databaseName = $"phase12_commit4_upgrade_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;
        var legacyQuoteId = Guid.NewGuid();
        var legacyReceiptId = Guid.NewGuid();

        await CreateDatabaseAsync(environment.PostgresConnectionString, databaseName);
        try
        {
            await using var reservation = CreateReservationContext(connectionString);
            await MigrateToAsync(reservation, "20260720084003_Phase12AuthoritativeCheckout");
            await InsertLegacyReservationRowsAsync(
                connectionString,
                legacyQuoteId,
                legacyReceiptId);

            await reservation.Database.MigrateAsync();
            reservation.ChangeTracker.Clear();

            var migratedQuote = await reservation.HoldQuotes
                .AsNoTracking()
                .SingleAsync(quote => quote.Id == legacyQuoteId);
            Assert.Equal(HoldQuoteState.Invalidated, migratedQuote.State);
            Assert.True(migratedQuote.Generation > 0);
            Assert.Null(migratedQuote.OrderId);
            Assert.NotNull(migratedQuote.TerminalAt);

            var migratedReceipt = await ReadMigratedReceiptAsync(connectionString, legacyReceiptId);
            Assert.Equal(64, migratedReceipt.RequestFingerprint.Length);
            Assert.Equal("text", migratedReceipt.DatabaseType);
            Assert.Equal("legacy-success", migratedReceipt.Outcome);

            // Expand-only gate: a Commit-3 writer still omits generation/state/fingerprint and
            // sends a varchar outcome after the Commit-4 schema is already at head.
            var rollingQuoteId = Guid.NewGuid();
            var rollingReceiptId = Guid.NewGuid();
            await InsertLegacyReservationRowsAsync(connectionString, rollingQuoteId, rollingReceiptId);
            reservation.ChangeTracker.Clear();
            var rollingQuote = await reservation.HoldQuotes.AsNoTracking()
                .SingleAsync(quote => quote.Id == rollingQuoteId);
            Assert.Equal(HoldQuoteState.Active, rollingQuote.State);
            Assert.True(rollingQuote.Generation > 0);
            Assert.Null(rollingQuote.OrderId);
            Assert.Null(rollingQuote.TerminalAt);
            var rollingReceipt = await ReadMigratedReceiptAsync(connectionString, rollingReceiptId);
            Assert.Equal(string.Empty, rollingReceipt.RequestFingerprint);
            Assert.Equal("legacy-success", rollingReceipt.Outcome);
            Assert.Equal("text", rollingReceipt.DatabaseType);

            await AssertHoldQuoteLifecycleAndOrderConstraintsAsync(connectionString);
            Assert.Empty(await reservation.Database.GetPendingMigrationsAsync());
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

    private static IdentityDbContext CreateIdentityContext(string connectionString) => new(
        new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);

    private static Task MigrateToAsync(DbContext context, string migrationId) =>
        context.Database.GetService<IMigrator>().MigrateAsync(migrationId);

    private static async Task<Exception?> SaveRegistrationAsync(IdentityDbContext context)
    {
        try
        {
            await new TicketFlow.Identity.Infrastructure.UnitOfWork(context).SaveChangesAsync();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

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
                     "SELECT id, display_name, roles, email FROM identity.users LIMIT 0",
                     "SELECT registration_key, normalized_email, status, keycloak_user_id FROM identity.user_registrations LIMIT 0",
                 })
        {
            await using var command = new NpgsqlCommand(query, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertLegacyReservationRowsAsync(
        string connectionString,
        Guid quoteId,
        Guid receiptId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var quoteCommand = new NpgsqlCommand(
                         """
                         INSERT INTO reservation.hold_quotes
                             (id, event_id, owner_user_id, seat_ids, amount, currency,
                              price_snapshot_version, expires_at, created_at)
                         VALUES
                             (@id, @event_id, @owner_user_id, @seat_ids, @amount, @currency,
                              @price_snapshot_version, @expires_at, @created_at)
                         """,
                         connection))
        {
            quoteCommand.Parameters.AddWithValue("id", quoteId);
            quoteCommand.Parameters.AddWithValue("event_id", Guid.NewGuid());
            quoteCommand.Parameters.AddWithValue("owner_user_id", Guid.NewGuid());
            quoteCommand.Parameters.AddWithValue("seat_ids", new[] { Guid.NewGuid(), Guid.NewGuid() });
            quoteCommand.Parameters.AddWithValue("amount", 300_000m);
            quoteCommand.Parameters.AddWithValue("currency", "VND");
            quoteCommand.Parameters.AddWithValue("price_snapshot_version", 7L);
            quoteCommand.Parameters.AddWithValue("expires_at", now.AddMinutes(5));
            quoteCommand.Parameters.AddWithValue("created_at", now);
            await quoteCommand.ExecuteNonQueryAsync();
        }

        await using var receiptCommand = new NpgsqlCommand(
            """
            INSERT INTO reservation.reservation_operation_receipts
                (id, operation_type, business_key, outcome, created_at, completed_at)
            VALUES
                (@id, @operation_type, @business_key, @outcome, @created_at, @completed_at)
            """,
            connection);
        receiptCommand.Parameters.AddWithValue("id", receiptId);
        receiptCommand.Parameters.AddWithValue("operation_type", "ConfirmSeats");
        receiptCommand.Parameters.AddWithValue("business_key", $"legacy:{quoteId:N}");
        receiptCommand.Parameters.AddWithValue("outcome", "legacy-success");
        receiptCommand.Parameters.AddWithValue("created_at", now);
        receiptCommand.Parameters.AddWithValue("completed_at", now);
        await receiptCommand.ExecuteNonQueryAsync();
    }

    private static async Task<MigratedReceiptRow> ReadMigratedReceiptAsync(
        string connectionString,
        Guid receiptId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT request_fingerprint, outcome::text, pg_typeof(outcome)::text
            FROM reservation.reservation_operation_receipts
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", receiptId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new MigratedReceiptRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2));
    }

    private static async Task AssertHoldQuoteLifecycleAndOrderConstraintsAsync(string connectionString)
    {
        var now = DateTimeOffset.UtcNow;
        var orderId = Guid.NewGuid();
        await InsertCurrentQuoteAsync(
            connectionString,
            Guid.NewGuid(),
            orderId,
            "Consumed",
            now);

        var duplicateOrder = await Assert.ThrowsAsync<PostgresException>(() => InsertCurrentQuoteAsync(
            connectionString,
            Guid.NewGuid(),
            orderId,
            "Consumed",
            now));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateOrder.SqlState);

        var invalidLifecycle = await Assert.ThrowsAsync<PostgresException>(() => InsertCurrentQuoteAsync(
            connectionString,
            Guid.NewGuid(),
            null,
            "Active",
            now));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidLifecycle.SqlState);
    }

    private static async Task InsertCurrentQuoteAsync(
        string connectionString,
        Guid quoteId,
        Guid? orderId,
        string state,
        DateTimeOffset? terminalAt)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO reservation.hold_quotes
                (id, event_id, owner_user_id, seat_ids, amount, currency,
                 price_snapshot_version, generation, state, order_id,
                 expires_at, created_at, terminal_at)
            VALUES
                (@id, @event_id, @owner_user_id, @seat_ids, @amount, @currency,
                 @price_snapshot_version, @generation, @state, @order_id,
                 @expires_at, @created_at, @terminal_at)
            """,
            connection);
        command.Parameters.AddWithValue("id", quoteId);
        command.Parameters.AddWithValue("event_id", Guid.NewGuid());
        command.Parameters.AddWithValue("owner_user_id", Guid.NewGuid());
        command.Parameters.AddWithValue("seat_ids", new[] { Guid.NewGuid() });
        command.Parameters.AddWithValue("amount", 100_000m);
        command.Parameters.AddWithValue("currency", "VND");
        command.Parameters.AddWithValue("price_snapshot_version", 9L);
        command.Parameters.AddWithValue("generation", 10_000L);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("order_id", (object?)orderId ?? DBNull.Value);
        command.Parameters.AddWithValue("expires_at", now.AddMinutes(10));
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("terminal_at", (object?)terminalAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
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

    private sealed record MigratedReceiptRow(
        string RequestFingerprint,
        string Outcome,
        string DatabaseType);
}
