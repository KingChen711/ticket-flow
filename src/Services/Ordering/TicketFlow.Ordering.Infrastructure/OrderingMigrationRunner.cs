using JasperFx;
using Marten;
using Npgsql;

namespace TicketFlow.Ordering.Infrastructure;

public static class OrderingMigrationRunner
{
    public const string SchemaVersion = "phase12-ordering-20260727-v1";

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

            // The application init gate reads this marker, not merely Marten's base tables.
            // Existing deployments already have mt_events/mt_streams before an upgrade begins;
            // only a marker written after every configured document/index change proves that
            // this exact migration contract completed.
            await using var markComplete = new NpgsqlCommand(
                """
                CREATE TABLE IF NOT EXISTS public.ticketflow_schema_versions (
                    component text PRIMARY KEY,
                    version text NOT NULL,
                    applied_at timestamptz NOT NULL
                );
                INSERT INTO public.ticketflow_schema_versions (component, version, applied_at)
                VALUES ('ordering', @version, now())
                ON CONFLICT (component) DO UPDATE
                SET version = EXCLUDED.version, applied_at = EXCLUDED.applied_at;
                """,
                migrationLock);
            markComplete.Parameters.AddWithValue("version", SchemaVersion);
            await markComplete.ExecuteNonQueryAsync(cancellationToken);
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
