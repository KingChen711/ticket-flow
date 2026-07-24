using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Events;
using TicketFlow.Catalog.Domain.SeatMaps;
using TicketFlow.Catalog.Domain.Venues;
using TicketFlow.Catalog.Domain.Workflows;
using TicketFlow.Contracts.Messaging.Catalog;

namespace TicketFlow.Catalog.Application.Workflows;

/// <summary>
/// Backend owner of Venue -> Event -> SeatMap -> pricing -> Reservation inventory -> Publish.
/// The browser submits one immutable intent; this service persists each boundary and reconciles
/// the exact ids on resume.
/// </summary>
public sealed class EventOnboardingWorkflowService(
    IEventOnboardingWorkflowRepository workflows,
    IVenueRepository venues,
    IEventRepository events,
    ISeatMapRepository seatMaps,
    IReservationInventoryClient reservation,
    IIntegrationEventPublisher publisher,
    IWorkflowFailureInjector failureInjector,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<EventOnboardingWorkflowResponse> StartAsync(
        StartEventOnboardingRequest request,
        CatalogCaller caller,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request);
        var fingerprint = Fingerprint(normalized);
        var workflow = await workflows.GetByCreationKeyAsync(normalized.CreationKey, cancellationToken);

        if (workflow is null)
        {
            workflow = EventOnboardingWorkflow.Start(
                normalized.CreationKey,
                fingerprint,
                normalized.ExistingVenueId,
                normalized.NewVenue is null
                    ? null
                    : new OnboardingVenuePlan(
                        normalized.NewVenue.Name,
                        normalized.NewVenue.Street,
                        normalized.NewVenue.City,
                        normalized.NewVenue.Country,
                        normalized.NewVenue.PostalCode),
                new OnboardingEventPlan(
                    normalized.Title,
                    normalized.Description,
                    normalized.Artist,
                    normalized.StartsAt,
                    normalized.EndsAt),
                normalized.Sections
                    .Select(section => new OnboardingSectionPlan(
                        section.Name, section.Capacity, section.Price, section.Currency!))
                    .ToList(),
                timeProvider.GetUtcNow(),
                caller.OrganizerId);

            workflows.Add(workflow);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyConflictException)
            {
                // Two requests may both observe a missing key. The unique CreationKey index picks
                // one durable winner; the loser must join that workflow instead of surfacing a
                // transient 409/500 that encourages the browser to invent another key.
                unitOfWork.DiscardChanges();
                workflow = await workflows.GetByCreationKeyAsync(normalized.CreationKey, cancellationToken);
                if (workflow is null)
                    throw;
            }
        }

        if (!string.Equals(workflow.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Creation key '{normalized.CreationKey}' is already bound to a different request.");
        }

        return await ContinueAsync(workflow, cancellationToken);
    }

    public async Task<EventOnboardingWorkflowResponse> ResumeAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var workflow = await GetRequiredAsync(workflowId, cancellationToken);
        return await ContinueAsync(workflow, cancellationToken);
    }

    public async Task<EventOnboardingWorkflowResponse?> GetAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var workflow = await workflows.GetByIdAsync(workflowId, cancellationToken);
        return workflow is null ? null : Map(workflow);
    }

    public async Task<EventOnboardingWorkflowResponse?> GetByCreationKeyAsync(
        string creationKey,
        CancellationToken cancellationToken = default)
    {
        var workflow = await workflows.GetByCreationKeyAsync(creationKey.Trim(), cancellationToken);
        return workflow is null ? null : Map(workflow);
    }

    public async Task<EventOnboardingWorkflowResponse> CompensateAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var workflow = await GetRequiredAsync(workflowId, cancellationToken);
        if (workflow.Status == EventOnboardingStatus.Compensated)
            return Map(workflow);

        var @event = await events.GetByIdAsync(workflow.EventId, cancellationToken);
        if (@event?.Status == EventStatus.Published)
            throw new InvalidOperationException("A published event must use the event-cancellation workflow.");

        // Persist a cancellation fence before touching Reservation. Without it, the legacy
        // publish endpoint could publish this draft after our status check but before remote
        // cleanup, leaving a published event whose inventory had just been deleted. The Event
        // xmin makes a concurrent publish/compensate race choose exactly one winner.
        workflow.BeginCompensation(timeProvider.GetUtcNow());
        if (@event?.Status == EventStatus.Draft)
            @event.Cancel();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            // Cleanup is itself idempotent. Calling it even when the persisted step precedes
            // inventory covers the important uncertain boundary: Reservation may have committed
            // while Catalog lost the response before advancing LastCompletedStep.
            await reservation.CleanupAsync(workflow.EventId, cancellationToken);

            @event = await events.GetByIdAsync(workflow.EventId, cancellationToken);
            if (@event is not null)
            {
                if (@event.Status == EventStatus.Published)
                    throw new InvalidOperationException("A published event must use the event-cancellation workflow.");
                events.Remove(@event);
            }

            var seatMap = await seatMaps.GetByEventIdAsync(workflow.EventId, cancellationToken);
            if (seatMap is not null)
                seatMaps.Remove(seatMap);

            if (workflow.CreatesVenue)
            {
                var venue = await venues.GetByIdAsync(workflow.VenueId, cancellationToken);
                if (venue is not null)
                    venues.Remove(venue);
            }

            workflow.MarkCompensated(timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Map(workflow);
        }
        catch (Exception exception)
        {
            unitOfWork.DiscardChanges();
            workflow = await GetRequiredAsync(workflowId, CancellationToken.None);
            // SaveChanges may have committed and only lost its acknowledgement. The durable
            // state wins; never regress a successfully compensated workflow to Failed.
            if (workflow.Status == EventOnboardingStatus.Compensated)
                return Map(workflow);

            workflow.FailCompensation(exception.Message, timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            return Map(workflow);
        }
    }

    private async Task<EventOnboardingWorkflowResponse> ContinueAsync(
        EventOnboardingWorkflow workflow,
        CancellationToken cancellationToken)
    {
        if (workflow.Status is EventOnboardingStatus.Completed or EventOnboardingStatus.Compensated)
            return Map(workflow);

        workflow.Resume(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        while (workflow.LastCompletedStep != EventOnboardingStep.Published)
        {
            var nextStep = (EventOnboardingStep)((int)workflow.LastCompletedStep + 1);
            try
            {
                await failureInjector.BeforeStepAsync(workflow.Id, nextStep, cancellationToken);
                await ExecuteStepAsync(workflow, nextStep, cancellationToken);
                workflow.CompleteStep(nextStep, timeProvider.GetUtcNow());
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                unitOfWork.DiscardChanges();
                workflow = await GetRequiredAsync(workflow.Id, CancellationToken.None);

                // If the local transaction actually committed and only its response was lost,
                // the persisted step wins. Never regress it to Failed.
                if (workflow.LastCompletedStep < nextStep)
                {
                    workflow.Fail(nextStep, exception.Message, timeProvider.GetUtcNow());
                    await unitOfWork.SaveChangesAsync(CancellationToken.None);
                }

                return Map(workflow);
            }
        }

        return Map(workflow);
    }

    private async Task ExecuteStepAsync(
        EventOnboardingWorkflow workflow,
        EventOnboardingStep step,
        CancellationToken cancellationToken)
    {
        switch (step)
        {
            case EventOnboardingStep.Venue:
                {
                    var venue = await venues.GetByIdAsync(workflow.VenueId, cancellationToken);
                    if (venue is null)
                    {
                        if (!workflow.CreatesVenue)
                            throw new InvalidOperationException($"Venue '{workflow.VenueId}' was not found.");

                        venues.Add(Venue.Register(
                            workflow.VenueId,
                            workflow.VenueName!,
                            Address.Create(
                                workflow.VenueStreet!,
                                workflow.VenueCity!,
                                workflow.VenueCountry!,
                                workflow.VenuePostalCode)));
                    }

                    break;
                }
            case EventOnboardingStep.Event:
                {
                    if (await events.GetByIdAsync(workflow.EventId, cancellationToken) is null)
                    {
                        events.Add(Event.Create(
                            workflow.EventId,
                            workflow.EventTitle,
                            workflow.EventDescription,
                            workflow.EventArtist,
                            workflow.VenueId,
                            EventSchedule.Create(workflow.StartsAt, workflow.EndsAt),
                            workflow.OrganizerId));
                    }

                    break;
                }
            case EventOnboardingStep.SeatMap:
                {
                    var seatMap = await seatMaps.GetByEventIdAsync(workflow.EventId, cancellationToken);
                    if (seatMap is null)
                        seatMaps.Add(SeatMap.CreateForEvent(workflow.SeatMapId, workflow.EventId));
                    else if (seatMap.Id != workflow.SeatMapId)
                        throw new InvalidOperationException("This event is already bound to another seat map.");
                    break;
                }
            case EventOnboardingStep.SectionsAndPricing:
                {
                    var seatMap = await seatMaps.GetByEventIdAsync(workflow.EventId, cancellationToken)
                        ?? throw new InvalidOperationException("Seat map is missing.");
                    var @event = await events.GetByIdAsync(workflow.EventId, cancellationToken)
                        ?? throw new InvalidOperationException("Event is missing.");

                    foreach (var planned in workflow.Sections.OrderBy(section => section.Position))
                    {
                        var section = seatMap.Sections.SingleOrDefault(candidate => candidate.Id == planned.SectionId);
                        if (section is null)
                        {
                            seatMap.AddSection(planned.SectionId, planned.Name, planned.Capacity);
                        }
                        else if (!string.Equals(section.Name, planned.Name, StringComparison.Ordinal) ||
                                 section.Capacity != planned.Capacity)
                        {
                            throw new InvalidOperationException($"Section '{planned.SectionId}' differs from the persisted plan.");
                        }

                        var tier = @event.PriceTiers.SingleOrDefault(candidate => candidate.Id == planned.PriceTierId);
                        if (tier is null)
                        {
                            @event.AddPriceTier(
                                planned.PriceTierId,
                                planned.SectionId,
                                Money.Create(planned.Price, planned.Currency));
                        }
                        else if (tier.SectionId != planned.SectionId || tier.Price.Amount != planned.Price ||
                                 !string.Equals(tier.Price.Currency, planned.Currency, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"Price tier '{planned.PriceTierId}' differs from the persisted plan.");
                        }
                    }

                    break;
                }
            case EventOnboardingStep.ReservationInventory:
                await reservation.ProvisionAsync(
                    workflow.EventId,
                    workflow.Sections
                        .OrderBy(section => section.Position)
                        .Select(section => new ReservationInventorySection(
                            section.SectionId, section.Name, section.Capacity))
                        .ToList(),
                    cancellationToken);
                break;
            case EventOnboardingStep.Published:
                await PublishAsync(workflow, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported onboarding step '{step}'.");
        }
    }

    private async Task PublishAsync(EventOnboardingWorkflow workflow, CancellationToken cancellationToken)
    {
        var @event = await events.GetByIdAsync(workflow.EventId, cancellationToken)
            ?? throw new InvalidOperationException("Event is missing.");
        if (@event.Status == EventStatus.Published)
            return;
        if (@event.Status == EventStatus.Cancelled)
            throw new InvalidOperationException("A cancelled event cannot be published.");

        var now = timeProvider.GetUtcNow();
        @event.Publish(now);
        var venue = await venues.GetByIdAsync(@event.VenueId, cancellationToken)
            ?? throw new InvalidOperationException("Venue is missing.");

        await publisher.PublishAsync(
            new CatalogEventPublished(
                @event.Id,
                @event.Title,
                @event.VenueId,
                @event.Schedule.StartsAt,
                now,
                @event.Artist,
                venue.Name,
                venue.Address.City,
                @event.AggregateVersion,
                now,
                @event.OrganizerId),
            cancellationToken);

        await publisher.PublishAsync(
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
    }

    private async Task<EventOnboardingWorkflow> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await workflows.GetByIdAsync(id, cancellationToken)
        ?? throw new InvalidOperationException($"Onboarding workflow '{id}' was not found.");

    private static StartEventOnboardingRequest Normalize(StartEventOnboardingRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.CreationKey) || request.CreationKey.Trim().Length > 128)
            throw new ArgumentException("Creation key is required and cannot exceed 128 characters.");
        if ((request.ExistingVenueId is null) == (request.NewVenue is null))
            throw new ArgumentException("Specify exactly one of ExistingVenueId or NewVenue.");
        if (request.Sections is not { Count: > 0 })
            throw new ArgumentException("At least one section is required.");

        var sections = request.Sections.Select(section => new EventOnboardingSectionRequest(
            Required(section.Name, "Section name"),
            section.Capacity > 0 ? section.Capacity : throw new ArgumentException("Section capacity must be positive."),
            section.Price >= 0 ? section.Price : throw new ArgumentException("Section price cannot be negative."),
            NormalizeCurrency(section.Currency))).ToList();

        if (sections.Select(section => section.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sections.Count)
            throw new ArgumentException("Section names must be unique.");

        var newVenue = request.NewVenue is null
            ? null
            : new NewVenueDefinition(
                Required(request.NewVenue.Name, "Venue name"),
                Required(request.NewVenue.Street, "Venue street"),
                Required(request.NewVenue.City, "Venue city"),
                Required(request.NewVenue.Country, "Venue country"),
                string.IsNullOrWhiteSpace(request.NewVenue.PostalCode) ? null : request.NewVenue.PostalCode.Trim());

        _ = EventSchedule.Create(request.StartsAt, request.EndsAt);

        return request with
        {
            CreationKey = request.CreationKey.Trim(),
            NewVenue = newVenue,
            Title = Required(request.Title, "Event title"),
            Description = request.Description?.Trim() ?? string.Empty,
            Artist = Required(request.Artist, "Event artist"),
            Sections = sections,
        };
    }

    private static string Fingerprint(StartEventOnboardingRequest request)
    {
        var canonical = JsonSerializer.Serialize(request with { CreationKey = string.Empty });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{field} is required.") : value.Trim();

    private static string NormalizeCurrency(string? currency)
    {
        var normalized = string.IsNullOrWhiteSpace(currency) ? "VND" : currency.Trim().ToUpperInvariant();
        return normalized.Length == 3 ? normalized : throw new ArgumentException("Currency must be a 3-letter code.");
    }

    private static EventOnboardingWorkflowResponse Map(EventOnboardingWorkflow workflow) => new(
        workflow.Id,
        workflow.CreationKey,
        workflow.VenueId,
        workflow.EventId,
        workflow.Status.ToString(),
        workflow.LastCompletedStep.ToString(),
        workflow.FailedStep?.ToString(),
        workflow.LastError,
        // Running is persisted before a step starts. If the process dies at that boundary the
        // workflow is still safe to resume (and, while unpublished, safe to compensate).
        workflow.Status is EventOnboardingStatus.Running or EventOnboardingStatus.Failed,
        workflow.Status is EventOnboardingStatus.Running or EventOnboardingStatus.Failed
            or EventOnboardingStatus.CompensationFailed,
        workflow.UpdatedAt);
}
