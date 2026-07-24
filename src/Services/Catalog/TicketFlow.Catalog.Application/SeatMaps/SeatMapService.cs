using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.SeatMaps;

namespace TicketFlow.Catalog.Application.SeatMaps;

public sealed class SeatMapService(
    ISeatMapRepository seatMapRepository,
    IEventRepository eventRepository,
    IUnitOfWork unitOfWork)
{
    public async Task<SeatMapResponse> CreateForEventAsync(
        Guid eventId,
        CatalogCaller caller,
        CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(eventId, caller, cancellationToken);

        if (await seatMapRepository.GetByEventIdAsync(eventId, cancellationToken) is not null)
            throw new InvalidOperationException($"Event '{eventId}' already has a seat map.");

        var seatMap = SeatMap.CreateForEvent(eventId);

        seatMapRepository.Add(seatMap);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(seatMap);
    }

    public async Task<SeatMapResponse> AddSectionAsync(
        Guid eventId,
        AddSectionRequest request,
        CatalogCaller caller,
        CancellationToken cancellationToken = default)
    {
        await EnsureOwnedAsync(eventId, caller, cancellationToken);

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

    /// <summary>
    /// A seat map is part of an event, so it inherits that event's ownership (Commit 14) — there
    /// is no separate owner to check. Without this, organizer B could add sections to organizer
    /// A's seat map, which then become price-tier targets on A's event.
    /// </summary>
    private async Task EnsureOwnedAsync(Guid eventId, CatalogCaller caller, CancellationToken cancellationToken)
    {
        var @event = await eventRepository.GetByIdAsync(eventId, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{eventId}' was not found.");
        if (!caller.IsAdmin && !@event.IsOwnedBy(caller.OrganizerId))
            throw new EventOwnershipException(eventId);
    }

    private static SeatMapResponse Map(SeatMap seatMap) => new(
        seatMap.Id,
        seatMap.EventId,
        seatMap.Sections.Select(s => new SectionResponse(s.Id, s.Name, s.Capacity)).ToList());
}
