using JasperFx;
using Marten;
using Npgsql;

namespace TicketFlow.Ordering.Infrastructure;

public static class OrderingMigrationRunner
{
    public static async Task RunAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var migrationLock = new NpgsqlConnection(connectionString);
        await migrationLock.OpenAsync(cancellationToken);

        await using var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext('ticketflow:migration:ordering'))",
            migrationLock);
        await acquire.ExecuteScalarAsync(cancellationToken);

        try
        {
            using var store = DocumentStore.For(options =>
            {
                options.Connection(connectionString);
                ServiceCollectionExtensions.Configure(options, AutoCreate.None);
            });
            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate.CreateOrUpdate);
        }
        finally
        {
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext('ticketflow:migration:ordering'))",
                migrationLock);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }
}
