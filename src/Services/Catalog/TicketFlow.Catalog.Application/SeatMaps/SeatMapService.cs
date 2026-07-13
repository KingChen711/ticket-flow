using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.SeatMaps;

namespace TicketFlow.Catalog.Application.SeatMaps;

public sealed class SeatMapService(
    ISeatMapRepository seatMapRepository,
    IEventRepository eventRepository,
    IUnitOfWork unitOfWork)
{
    public async Task<SeatMapResponse> CreateForEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        _ = await eventRepository.GetByIdAsync(eventId, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{eventId}' was not found.");

        if (await seatMapRepository.GetByEventIdAsync(eventId, cancellationToken) is not null)
            throw new InvalidOperationException($"Event '{eventId}' already has a seat map.");

        var seatMap = SeatMap.CreateForEvent(eventId);

        seatMapRepository.Add(seatMap);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(seatMap);
    }

    public async Task<SeatMapResponse> AddSectionAsync(Guid eventId, AddSectionRequest request, CancellationToken cancellationToken = default)
    {
        var seatMap = await seatMapRepository.GetByEventIdAsync(eventId, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{eventId}' does not have a seat map yet.");

        seatMap.AddSection(request.Name, request.Capacity);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(seatMap);
    }

    public async Task<SeatMapResponse?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var seatMap = await seatMapRepository.GetByEventIdAsync(eventId, cancellationToken);
        return seatMap is null ? null : Map(seatMap);
    }

    private static SeatMapResponse Map(SeatMap seatMap) => new(
        seatMap.Id,
        seatMap.EventId,
        seatMap.Sections.Select(s => new SectionResponse(s.Id, s.Name, s.Capacity)).ToList());
}
