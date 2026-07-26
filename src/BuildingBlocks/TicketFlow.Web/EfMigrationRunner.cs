using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace TicketFlow.Web;

/// <summary>
/// Runs one EF migration process under a PostgreSQL session advisory lock. Production pods never
/// call this from normal startup; the Helm migration Job invokes the same application image with
/// <c>--migrate</c>, so schema code and deployed binaries cannot drift.
/// </summary>
public static class EfMigrationRunner
{
    public const string Command = "--migrate";

    public static bool IsRequested(IEnumerable<string> args) =>
        args.Contains(Command, StringComparer.Ordinal);

    public static async Task RunAsync(
        DbContext dbContext,
        string component,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        var connection = dbContext.Database.GetDbConnection();
        var lockName = $"ticketflow:migration:{component}";

        await ExecuteAsync(connection, "SELECT pg_advisory_lock(hashtext(@lock_name))", lockName, cancellationToken);
        try
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        finally
        {
            await ExecuteAsync(
                connection,
                "SELECT pg_advisory_unlock(hashtext(@lock_name))",
                lockName,
                CancellationToken.None);
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        string lockName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_name";
        parameter.Value = lockName;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
