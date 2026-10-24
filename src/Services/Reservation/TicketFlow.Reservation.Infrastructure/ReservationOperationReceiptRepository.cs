using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Allocations;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class ReservationOperationReceiptRepository(ReservationDbContext dbContext)
    : IReservationOperationReceiptRepository
{
    public Task<IAsyncDisposable> AcquireLockAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default) =>
        AcquireLockCoreAsync(scope, key, shared: false, cancellationToken);

    public Task<IAsyncDisposable> AcquireSharedLockAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default) =>
        AcquireLockCoreAsync(scope, key, shared: true, cancellationToken);

    private async Task<IAsyncDisposable> AcquireLockCoreAsync(
        string scope,
        string key,
        bool shared,
        CancellationToken cancellationToken)
    {
        var lockKey = $"{scope}:{key}";
        var currentTransaction = dbContext.Database.CurrentTransaction;
        if (currentTransaction is not null)
        {
            await ExecuteLockCommandAsync(
                dbContext.Database.GetDbConnection(),
                currentTransaction.GetDbTransaction(),
                shared ? "pg_advisory_xact_lock_shared" : "pg_advisory_xact_lock",
                lockKey,
                cancellationToken);
            return NoopReservationAuthorityLock.Instance;
        }

        var connection = dbContext.Database.GetDbConnection();
        var closeWhenReleased = connection.State != ConnectionState.Open;
        if (closeWhenReleased)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await ExecuteLockCommandAsync(
                connection,
                transaction: null,
                shared ? "pg_advisory_lock_shared" : "pg_advisory_lock",
                lockKey,
                cancellationToken);
            return new SessionAdvisoryLock(
                connection,
                lockKey,
                shared ? "pg_advisory_unlock_shared" : "pg_advisory_unlock",
                closeWhenReleased);
        }
        catch
        {
            if (closeWhenReleased)
                await connection.CloseAsync();
            throw;
        }
    }

    public Task<ReservationOperationReceipt?> GetAsync(
        string operationType,
        string businessKey,
        CancellationToken cancellationToken = default) =>
        dbContext.ReservationOperationReceipts.SingleOrDefaultAsync(
            receipt => receipt.OperationType == operationType && receipt.BusinessKey == businessKey,
            cancellationToken);

    public void Add(ReservationOperationReceipt receipt) =>
        dbContext.ReservationOperationReceipts.Add(receipt);

    private static async Task ExecuteLockCommandAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string function,
        string lockKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {function}(hashtextextended(@lock_key, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = lockKey;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class SessionAdvisoryLock(
        DbConnection connection,
        string lockKey,
        string unlockFunction,
        bool closeWhenReleased) : IAsyncDisposable
    {
        private bool _released;

        public async ValueTask DisposeAsync()
        {
            if (_released)
                return;

            _released = true;
            try
            {
                await ExecuteLockCommandAsync(
                    connection,
                    transaction: null,
                    unlockFunction,
                    lockKey,
                    CancellationToken.None);
            }
            finally
            {
                if (closeWhenReleased)
                    await connection.CloseAsync();
            }
        }
    }
}
