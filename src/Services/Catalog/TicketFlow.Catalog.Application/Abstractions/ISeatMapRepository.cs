using TicketFlow.Catalog.Domain.SeatMaps;

namespace TicketFlow.Catalog.Application.Abstractions;

public interface ISeatMapRepository
{
    Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SeatMap?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default);
    void Add(SeatMap seatMap);
    void Remove(SeatMap seatMap) => throw new NotSupportedException("This seat-map repository does not support removal.");
}
