using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Abstractions;

/// <summary>Postgres side — the durable seat inventory + the sold record.</summary>
public interface ISeatRepository
{
    Task<bool> InventoryExistsAsync(Guid eventId, CancellationToken cancellationToken = default);
    void AddRange(IEnumerable<Seat> seats);
    Task<IReadOnlyList<Seat>> GetByEventAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Seat>> GetByIdsAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default);
}
