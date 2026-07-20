using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Events;
using TicketFlow.Contracts.Messaging.Catalog;

namespace TicketFlow.Catalog.Application.Events;

public sealed class EventService(
    IEventRepository eventRepository,
    IVenueRepository venueRepository,
    ISeatMapRepository seatMapRepository,
    IUnitOfWork unitOfWork,
    IIntegrationEventPublisher integrationEventPublisher,
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

        // Same reasoning as PublishAsync below: the domain event Event.Reschedule() raises is
        // in-process only (Ignore(DomainEvents)) — this is the real cross-service contract, and
        // it must publish BEFORE SaveChangesAsync so the Outbox interceptor captures it in the
        // same transaction as the reschedule write.
        await integrationEventPublisher.PublishAsync(
            new CatalogEventRescheduled(
                @event.Id, @event.Schedule.StartsAt, @event.Schedule.EndsAt,
                @event.AggregateVersion, timeProvider.GetUtcNow()),
            cancellationToken);

        if (@event.Status == EventStatus.Published)
            await PublishSalePolicyAsync(@event, timeProvider.GetUtcNow(), cancellationToken);

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
        var now = timeProvider.GetUtcNow();

        @event.Publish(now);

        // Translate domain event -> integration event here, not inside Event.Publish(): the
        // domain event (EventPublished) only carries the aggregate id and is for in-process
        // consumers; this integration event is the stable cross-service contract and needs
        // fields (Title, VenueId, StartsAt, and — Phase 6 — Artist/VenueName/City) the domain
        // event doesn't carry (see Commit 2). Venue is loaded here (Publish didn't need it
        // before Phase 6) purely to denormalize into the event — search must be able to index
        // artist/venue/city without calling back into catalog per event (see contract's doc
        // comment). Publish() must run BEFORE SaveChangesAsync() — it buffers the message
        // against this scope's CatalogDbContext, which SaveChangesAsync then flushes into
        // outbox_message as part of the same transaction.
        var venue = await venueRepository.GetByIdAsync(@event.VenueId, cancellationToken)
            ?? throw new InvalidOperationException($"Venue '{@event.VenueId}' was not found.");

        await integrationEventPublisher.PublishAsync(
            new CatalogEventPublished(
                @event.Id, @event.Title, @event.VenueId, @event.Schedule.StartsAt, now,
                @event.Artist, venue.Name, venue.Address.City,
                @event.AggregateVersion, now),
            cancellationToken);

        await PublishSalePolicyAsync(@event, now, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(@event);
    }

    public async Task<EventResponse> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var @event = await GetRequiredAsync(id, cancellationToken);

        @event.Cancel();

        // Same reasoning as RescheduleAsync/PublishAsync above.
        var now = timeProvider.GetUtcNow();
        await integrationEventPublisher.PublishAsync(
            new CatalogEventCancelled(@event.Id, @event.AggregateVersion, now),
            cancellationToken);
        await PublishSalePolicyAsync(@event, now, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(@event);
    }

    private async Task<Event> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await eventRepository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{id}' was not found.");

    private Task PublishSalePolicyAsync(Event @event, DateTimeOffset now, CancellationToken cancellationToken) =>
        integrationEventPublisher.PublishAsync(
            new CatalogSalePolicyChanged(
                @event.Id,
                @event.Status.ToString(),
                now,
                @event.Schedule.StartsAt,
                @event.PriceTiers
                    .Select(tier => new PriceTierSnapshotV2(
                        tier.SectionId, tier.Price.Amount, tier.Price.Currency))
                    .ToList(),
                @event.PriceSnapshotVersion,
                now),
            cancellationToken);

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
