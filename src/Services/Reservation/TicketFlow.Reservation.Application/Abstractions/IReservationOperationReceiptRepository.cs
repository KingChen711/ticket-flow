using TicketFlow.Reservation.Domain.Allocations;

namespace TicketFlow.Reservation.Application.Abstractions;

public interface IReservationOperationReceiptRepository
{
    /// <summary>
    /// Serializes a logical resource. The production repository uses a transaction-scoped lock
    /// when an ambient consumer transaction exists and a session lock for HTTP operations.
    /// </summary>
    Task<IAsyncDisposable> AcquireLockAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsyncDisposable>(NoopReservationAuthorityLock.Instance);

    /// <summary>Shared counterpart used by high-volume readers that must serialize with an
    /// exclusive policy/lifecycle writer without serializing with one another.</summary>
    Task<IAsyncDisposable> AcquireSharedLockAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsyncDisposable>(NoopReservationAuthorityLock.Instance);

    Task<ReservationOperationReceipt?> GetAsync(
        string operationType,
        string businessKey,
        CancellationToken cancellationToken = default);

    void Add(ReservationOperationReceipt receipt);
}

public sealed class NoopReservationAuthorityLock : IAsyncDisposable
{
    public static NoopReservationAuthorityLock Instance { get; } = new();

    private NoopReservationAuthorityLock()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
