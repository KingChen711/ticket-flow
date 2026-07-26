namespace TicketFlow.Catalog.Application.Events;

public sealed record CreateEventRequest(
    string Title,
    string Description,
    string Artist,
    Guid VenueId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? TimeZoneId = "UTC",
    int? StartUtcOffsetMinutes = null,
    int? EndUtcOffsetMinutes = null);

public sealed record RescheduleEventRequest(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? TimeZoneId = "UTC",
    int? StartUtcOffsetMinutes = null,
    int? EndUtcOffsetMinutes = null);

public sealed record AddPriceTierRequest(Guid SectionId, decimal Amount, string? Currency);

public sealed record PriceTierResponse(Guid Id, Guid SectionId, decimal Amount, string Currency);

public sealed record EventResponse(
    Guid Id,
    string Title,
    string Description,
    string Artist,
    Guid VenueId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    IReadOnlyList<PriceTierResponse> PriceTiers,
    string TimeZoneId);
