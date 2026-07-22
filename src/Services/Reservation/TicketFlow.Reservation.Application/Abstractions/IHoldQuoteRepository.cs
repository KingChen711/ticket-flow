using TicketFlow.Reservation.Domain.Holds;

namespace TicketFlow.Reservation.Application.Abstractions;

public interface IHoldQuoteRepository
{
    Task<HoldQuote?> GetAsync(Guid holdId, CancellationToken cancellationToken = default);

    Task<HoldQuote?> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HoldQuote>> GetActiveOverlappingAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetActiveIdsByEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    /// <summary>Active quotes this user currently holds for one event — the per-user cap input.</summary>
    Task<IReadOnlyList<HoldQuote>> GetActiveByOwnerAsync(
        Guid eventId,
        Guid ownerUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many quotes this user created for one event since <paramref name="since"/>, in ANY
    /// state. Counting terminal rows too is deliberate: releasing a hold immediately must not
    /// refill the rate-limit budget, or the limit is trivially bypassed by hold/release looping.
    /// </summary>
    Task<int> CountCreatedByOwnerSinceAsync(
        Guid eventId,
        Guid ownerUserId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    void Add(HoldQuote quote);
}
