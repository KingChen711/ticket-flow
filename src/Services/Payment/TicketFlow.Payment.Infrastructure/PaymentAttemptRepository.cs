using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Infrastructure;

public sealed class PaymentAttemptRepository(PaymentDbContext dbContext) : IPaymentAttemptRepository
{
    /// <summary>
    /// Same advisory-lock shape as Reservation's receipt repository: a transaction-scoped lock
    /// when a consumer transaction is ambient (MassTransit's EF outbox supplies one), otherwise a
    /// session lock released on dispose.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireOrderLockAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var lockKey = $"payment-order:{orderId:N}";
        var currentTransaction = dbContext.Database.CurrentTransaction;
        if (currentTransaction is not null)
        {
            await ExecuteLockCommandAsync(
                dbContext.Database.GetDbConnection(),
                currentTransaction.GetDbTransaction(),
                "pg_advisory_xact_lock",
                lockKey,
                cancellationToken);
            return NoopPaymentLock.Instance;
        }

        var connection = dbContext.Database.GetDbConnection();
        var closeWhenReleased = connection.State != ConnectionState.Open;
        if (closeWhenReleased)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await ExecuteLockCommandAsync(connection, null, "pg_advisory_lock", lockKey, cancellationToken);
            return new SessionAdvisoryLock(connection, lockKey, closeWhenReleased);
        }
        catch
        {
            if (closeWhenReleased)
                await connection.CloseAsync();
            throw;
        }
    }

    public Task<PaymentAttempt?> GetAttemptByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        dbContext.PaymentAttempts.SingleOrDefaultAsync(attempt => attempt.OrderId == orderId, cancellationToken);

    public Task<PaymentAttempt?> GetAttemptByIdAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        dbContext.PaymentAttempts.SingleOrDefaultAsync(attempt => attempt.Id == attemptId, cancellationToken);

    public async Task<IReadOnlyList<PaymentAttempt>> GetUnresolvedAttemptsAsync(
        int maxCount,
        CancellationToken cancellationToken = default) =>
        await dbContext.PaymentAttempts
            .Where(attempt =>
                attempt.State == PaymentAttemptState.Unknown ||
                attempt.State == PaymentAttemptState.Pending)
            .OrderBy(attempt => attempt.CreatedAt)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

    public void AddAttempt(PaymentAttempt attempt) => dbContext.PaymentAttempts.Add(attempt);

    public Task<PaymentRefund?> GetRefundByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        dbContext.PaymentRefunds.SingleOrDefaultAsync(refund => refund.OrderId == orderId, cancellationToken);

    public async Task<IReadOnlyList<PaymentRefund>> GetUnresolvedRefundsAsync(
        int maxCount,
        CancellationToken cancellationToken = default) =>
        await dbContext.PaymentRefunds
            .Where(refund =>
                refund.State == PaymentRefundState.Unknown ||
                refund.State == PaymentRefundState.Pending)
            .OrderBy(refund => refund.CreatedAt)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

    public void AddRefund(PaymentRefund refund) => dbContext.PaymentRefunds.Add(refund);

    public bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation)
                return true;
        }

        return false;
    }

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
                    connection, null, "pg_advisory_unlock", lockKey, CancellationToken.None);
            }
            finally
            {
                if (closeWhenReleased)
                    await connection.CloseAsync();
            }
        }
    }
}
