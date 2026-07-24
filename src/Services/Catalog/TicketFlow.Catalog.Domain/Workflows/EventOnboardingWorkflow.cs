using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Workflows;

public enum EventOnboardingStep
{
    Accepted = 0,
    Venue = 1,
    Event = 2,
    SeatMap = 3,
    SectionsAndPricing = 4,
    ReservationInventory = 5,
    Published = 6,
}

public enum EventOnboardingStatus
{
    Running,
    Failed,
    Completed,
    Compensating,
    CompensationFailed,
    Compensated,
}

public sealed record OnboardingVenuePlan(
    string Name,
    string Street,
    string City,
    string Country,
    string? PostalCode);

public sealed record OnboardingEventPlan(
    string Title,
    string Description,
    string Artist,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

public sealed record OnboardingSectionPlan(string Name, int Capacity, decimal Price, string Currency);

/// <summary>
/// Durable source of truth for the organizer's create-and-publish operation.  All aggregate ids
/// are allocated and committed with this row before the first side effect, which makes every
/// subsequent step addressable and replay-safe after an uncertain response.
/// </summary>
public sealed class EventOnboardingWorkflow : Entity<Guid>
{
    private readonly List<EventOnboardingSection> _sections = [];

    public string CreationKey { get; private set; } = null!;
    public string RequestFingerprint { get; private set; } = null!;
    public Guid VenueId { get; private set; }
    public bool CreatesVenue { get; private set; }
    public string? VenueName { get; private set; }
    public string? VenueStreet { get; private set; }
    public string? VenueCity { get; private set; }
    public string? VenueCountry { get; private set; }
    public string? VenuePostalCode { get; private set; }
    /// <summary>
    /// The organizer this onboarding belongs to (Commit 14), captured once at Start and stored on
    /// the workflow rather than read from whoever happens to call resume. A resumable workflow can
    /// be continued minutes later by a retry, so taking the owner from the resuming request would
    /// let a second organizer adopt a half-built event by resuming it.
    /// </summary>
    public Guid? OrganizerId { get; private set; }

    public Guid EventId { get; private set; }
    public Guid SeatMapId { get; private set; }
    public string EventTitle { get; private set; } = null!;
    public string EventDescription { get; private set; } = null!;
    public string EventArtist { get; private set; } = null!;
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public EventOnboardingStep LastCompletedStep { get; private set; }
    public EventOnboardingStep? FailedStep { get; private set; }
    public EventOnboardingStatus Status { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<EventOnboardingSection> Sections => _sections.AsReadOnly();

    private EventOnboardingWorkflow() { }

    public static EventOnboardingWorkflow Start(
        string creationKey,
        string requestFingerprint,
        Guid? existingVenueId,
        OnboardingVenuePlan? newVenue,
        OnboardingEventPlan eventPlan,
        IReadOnlyList<OnboardingSectionPlan> sections,
        DateTimeOffset now,
        Guid? organizerId = null)
    {
        if (string.IsNullOrWhiteSpace(creationKey))
            throw new ArgumentException("Creation key is required.", nameof(creationKey));
        if (string.IsNullOrWhiteSpace(requestFingerprint))
            throw new ArgumentException("Request fingerprint is required.", nameof(requestFingerprint));
        if (existingVenueId is null && newVenue is null)
            throw new ArgumentException("Either an existing venue or a new venue definition is required.");
        if (existingVenueId == Guid.Empty)
            throw new ArgumentException("Existing venue id cannot be empty.", nameof(existingVenueId));
        if (sections.Count == 0)
            throw new ArgumentException("At least one section is required.", nameof(sections));

        var workflow = new EventOnboardingWorkflow
        {
            Id = Guid.NewGuid(),
            CreationKey = creationKey.Trim(),
            RequestFingerprint = requestFingerprint,
            VenueId = existingVenueId ?? Guid.NewGuid(),
            CreatesVenue = existingVenueId is null,
            VenueName = newVenue?.Name,
            VenueStreet = newVenue?.Street,
            VenueCity = newVenue?.City,
            VenueCountry = newVenue?.Country,
            VenuePostalCode = newVenue?.PostalCode,
            OrganizerId = organizerId == Guid.Empty ? null : organizerId,
            EventId = Guid.NewGuid(),
            SeatMapId = Guid.NewGuid(),
            EventTitle = eventPlan.Title,
            EventDescription = eventPlan.Description,
            EventArtist = eventPlan.Artist,
            StartsAt = eventPlan.StartsAt,
            EndsAt = eventPlan.EndsAt,
            LastCompletedStep = EventOnboardingStep.Accepted,
            Status = EventOnboardingStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var index = 0; index < sections.Count; index++)
            workflow._sections.Add(EventOnboardingSection.Create(index, sections[index]));

        return workflow;
    }

    public void Resume(DateTimeOffset now)
    {
        if (Status is EventOnboardingStatus.Completed or EventOnboardingStatus.Compensated)
            return;
        if (Status is EventOnboardingStatus.Compensating or EventOnboardingStatus.CompensationFailed)
            throw new InvalidOperationException("A compensating workflow cannot be resumed.");

        Status = EventOnboardingStatus.Running;
        FailedStep = null;
        LastError = null;
        UpdatedAt = now;
    }

    public void CompleteStep(EventOnboardingStep step, DateTimeOffset now)
    {
        if (step < LastCompletedStep)
            return;
        if ((int)step > (int)LastCompletedStep + 1)
            throw new InvalidOperationException($"Cannot skip from {LastCompletedStep} to {step}.");

        LastCompletedStep = step;
        FailedStep = null;
        LastError = null;
        Status = step == EventOnboardingStep.Published
            ? EventOnboardingStatus.Completed
            : EventOnboardingStatus.Running;
        UpdatedAt = now;
    }

    public void Fail(EventOnboardingStep step, string error, DateTimeOffset now)
    {
        if (Status == EventOnboardingStatus.Completed)
            return;

        FailedStep = step;
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown workflow failure." : error[..Math.Min(error.Length, 2000)];
        Status = EventOnboardingStatus.Failed;
        UpdatedAt = now;
    }

    public void BeginCompensation(DateTimeOffset now)
    {
        if (Status == EventOnboardingStatus.Completed || LastCompletedStep == EventOnboardingStep.Published)
            throw new InvalidOperationException("A published event must use the event-cancellation workflow.");
        if (Status == EventOnboardingStatus.Compensated)
            return;

        Status = EventOnboardingStatus.Compensating;
        FailedStep = null;
        LastError = null;
        UpdatedAt = now;
    }

    public void MarkCompensated(DateTimeOffset now)
    {
        Status = EventOnboardingStatus.Compensated;
        UpdatedAt = now;
    }

    public void FailCompensation(string error, DateTimeOffset now)
    {
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown cleanup failure." : error[..Math.Min(error.Length, 2000)];
        Status = EventOnboardingStatus.CompensationFailed;
        UpdatedAt = now;
    }
}

public sealed class EventOnboardingSection
{
    public int Position { get; private set; }
    public Guid SectionId { get; private set; }
    public Guid PriceTierId { get; private set; }
    public string Name { get; private set; } = null!;
    public int Capacity { get; private set; }
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = null!;

    private EventOnboardingSection() { }

    internal static EventOnboardingSection Create(int position, OnboardingSectionPlan plan) => new()
    {
        Position = position,
        SectionId = Guid.NewGuid(),
        PriceTierId = Guid.NewGuid(),
        Name = plan.Name,
        Capacity = plan.Capacity,
        Price = plan.Price,
        Currency = plan.Currency,
    };
}
