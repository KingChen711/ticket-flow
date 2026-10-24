namespace TicketFlow.Catalog.Application.Workflows;

public sealed record NewVenueDefinition(
    string Name,
    string Street,
    string City,
    string Country,
    string? PostalCode);

public sealed record EventOnboardingSectionRequest(
    string Name,
    int Capacity,
    decimal Price,
    string? Currency = "VND");

public sealed record StartEventOnboardingRequest(
    string CreationKey,
    Guid? ExistingVenueId,
    NewVenueDefinition? NewVenue,
    string Title,
    string Description,
    string Artist,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    IReadOnlyList<EventOnboardingSectionRequest> Sections,
    string? TimeZoneId = "UTC",
    int? StartUtcOffsetMinutes = null,
    int? EndUtcOffsetMinutes = null);

public sealed record EventOnboardingWorkflowResponse(
    Guid WorkflowId,
    string CreationKey,
    Guid VenueId,
    Guid EventId,
    string Status,
    string LastCompletedStep,
    string? FailedStep,
    string? LastError,
    bool CanResume,
    bool CanCompensate,
    DateTimeOffset UpdatedAt);

public sealed record EventCancellationWorkflowResponse(
    Guid WorkflowId,
    Guid EventId,
    string Status,
    string Reason,
    int? OrderCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt);
