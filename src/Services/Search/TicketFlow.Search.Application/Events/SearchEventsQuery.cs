namespace TicketFlow.Search.Application.Events;

/// <summary>
/// All fields optional except paging — scope mục 4: "tìm sự kiện theo nghệ sĩ/venue/ngày/thành
/// phố". <see cref="Q"/> is free-text (title full-text match); Artist/City are exact-match
/// filters; From/To bound StartsAt. No VenueName filter — scope only asks for city, and venue
/// name has no dedicated filter param in the endpoint contract (still returned in results).
/// </summary>
public sealed record SearchEventsQuery(
    string? Q,
    string? Artist,
    string? City,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page = 1,
    int PageSize = 20);

public sealed record SearchEventsResult(IReadOnlyList<SearchDocument> Items, long Total);
