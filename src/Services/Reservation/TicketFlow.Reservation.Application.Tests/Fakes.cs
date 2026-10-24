using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Tests;

/// <summary>
/// In-memory stand-in for RedisSeatHoldStore with the same contract: all-or-nothing holds, confirm
/// moves only the seats still owned by the hold into the sold set. TTL expiry is simulated with
/// <see cref="Expire"/>. The real Lua scripts are covered by Reservation.IntegrationTests.
/// </summary>
public sealed class FakeSeatHoldStore : ISeatHoldStore
{
    private readonly Dictionary<Guid, Guid> _holds = [];
    private readonly HashSet<Guid> _sold = [];

    public TimeSpan? LastTtl { get; private set; }

    public Task LoadInventoryAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Guid>> TryHoldAsync(Guid eventId, IReadOnlyList<Guid> seatIds, Guid holdId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> unavailable = seatIds.Where(id => _holds.ContainsKey(id) || _sold.Contains(id)).ToList();
        if (unavailable.Count == 0)
        {
            foreach (var id in seatIds)
                _holds[id] = holdId;
            LastTtl = ttl;
        }

        return Task.FromResult(unavailable);
    }

    public Task<IReadOnlyList<Guid>> ReleaseAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> released = SeatsHeldBy(holdId);
        foreach (var id in released)
            _holds.Remove(id);

        return Task.FromResult(released);
    }

    public Task<IReadOnlyList<Guid>> ConfirmAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> confirmed = SeatsHeldBy(holdId);
        foreach (var id in confirmed)
        {
            _holds.Remove(id);
            _sold.Add(id);
        }

        return Task.FromResult(confirmed);
    }

    public Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        var statuses = new Dictionary<Guid, SeatStatus>();
        foreach (var id in seatIds)
        {
            if (_sold.Contains(id))
                statuses[id] = SeatStatus.Sold;
            else if (_holds.ContainsKey(id))
                statuses[id] = SeatStatus.Held;
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, SeatStatus>>(statuses);
    }

    public Task RemoveFromSoldAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in seatIds)
            _sold.Remove(id);

        return Task.CompletedTask;
    }

    /// <summary>Simulates Redis expiring the TTL key of one held seat.</summary>
    public void Expire(Guid seatId) => _holds.Remove(seatId);

    public bool IsSold(Guid seatId) => _sold.Contains(seatId);

    private List<Guid> SeatsHeldBy(Guid holdId) =>
        _holds.Where(pair => pair.Value == holdId).Select(pair => pair.Key).ToList();
}

public sealed class FakeSeatRepository : ISeatRepository
{
    public List<Seat> Seats { get; } = [];

    public Task<bool> InventoryExistsAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Seats.Any(seat => seat.EventId == eventId));

    public void AddRange(IEnumerable<Seat> seats) => Seats.AddRange(seats);

    public Task<IReadOnlyList<Seat>> GetByEventAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Seat>>(Seats.Where(seat => seat.EventId == eventId).ToList());

    public Task<IReadOnlyList<Seat>> GetByIdsAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Seat>>(Seats.Where(seat => seat.EventId == eventId && seatIds.Contains(seat.Id)).ToList());
}

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakeSeatMapNotifier : ISeatMapNotifier
{
    public List<(Guid EventId, IReadOnlyList<Guid> SeatIds, string Status)> Updates { get; } = [];

    public Task SeatsUpdatedAsync(Guid eventId, IReadOnlyList<Guid> seatIds, string status, CancellationToken cancellationToken = default)
    {
        Updates.Add((eventId, seatIds, status));
        return Task.CompletedTask;
    }
}

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
