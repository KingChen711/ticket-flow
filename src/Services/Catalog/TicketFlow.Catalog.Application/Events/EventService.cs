using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Events;

namespace TicketFlow.Catalog.Application.Events;

public sealed class EventService(
    IEventRepository eventRepository,
    IVenueRepository venueRepository,
    ISeatMapRepository seatMapRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<EventResponse> CreateAsync(CreateEventRequest request, CancellationToken cancellationToken = default)
    {
        var venue = await venueRepository.GetByIdAsync(request.VenueId, cancellationToken)
            ?? throw new InvalidOperationException($"Venue '{request.VenueId}' was not found.");

        var schedule = EventSchedule.Create(request.StartsAt, request.EndsAt);
        var @event = Event.Create(request.Title, request.Description, request.Artist, venue.Id, schedule);

        eventRepository.Add(@event);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(@event);
    }

    public async Task<EventResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var @event = await eventRepository.GetByIdAsync(id, cancellationToken);
        return @event is null ? null : Map(@event);
    }

    public async Task<IReadOnlyList<EventResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var events = await eventRepository.ListAsync(cancellationToken);
        return events.Select(Map).ToList();
    }

    public async Task<EventResponse> RescheduleAsync(Guid id, RescheduleEventRequest request, CancellationToken cancellationToken = default)
    {
        var @event = await GetRequiredAsync(id, cancellationToken);
        var schedule = EventSchedule.Create(request.StartsAt, request.EndsAt);

        @event.Reschedule(schedule, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(@event);
    }

    /// <summary>
    /// Cross-aggregate invariant lives here, not inside Event or SeatMap: a price tier's
    /// SectionId must correspond to a real Section in this event's own SeatMap.
    /// </summary>
    public async Task<EventResponse> AddPriceTierAsync(Guid id, AddPriceTierRequest request, CancellationToken cancellationToken = default)
    {
        var @event = await GetRequiredAsync(id, cancellationToken);

        var seatMap = await seatMapRepository.GetByEventIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{id}' does not have a seat map yet.");

        if (seatMap.Sections.All(section => section.Id != request.SectionId))
            throw new InvalidOperationException($"Section '{request.SectionId}' does not exist in this event's seat map.");

        var price = Money.Create(request.Amount, request.Currency ?? "VND");
        @event.AddPriceTier(request.SectionId, price);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(@event);
    }

    public async Task<EventResponse> PublishAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var @event = await GetRequiredAsync(id, cancellationToken);

        @event.Publish(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(@event);
    }

    public async Task<EventResponse> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var @event = await GetRequiredAsync(id, cancellationToken);

        @event.Cancel();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(@event);
    }

    private async Task<Event> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await eventRepository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{id}' was not found.");

    private static EventResponse Map(Event @event) => new(
        @event.Id,
        @event.Title,
        @event.Description,
        @event.Artist,
        @event.VenueId,
        @event.Schedule.StartsAt,
        @event.Schedule.EndsAt,
        @event.Status.ToString(),
        @event.PriceTiers
            .Select(p => new PriceTierResponse(p.Id, p.SectionId, p.Price.Amount, p.Price.Currency))
            .ToList());
}
