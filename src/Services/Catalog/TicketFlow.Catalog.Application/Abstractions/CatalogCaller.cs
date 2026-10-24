namespace TicketFlow.Catalog.Application.Abstractions;

/// <summary>
/// Who is asking, as far as Catalog is concerned (Phase 12 Commit 14).
/// </summary>
/// <remarks>
/// Before this commit the write endpoints only asked "does this caller hold the organizer role?",
/// which every organizer in the realm satisfies — so organizer B could reschedule, publish, cancel
/// or re-price organizer A's event just by knowing its id, and cancelling closes sale and voids
/// live holds immediately (Commit 3). A role says what KIND of thing you are; it says nothing
/// about WHICH events are yours, and this type is what carries the second half.
/// </remarks>
/// <param name="OrganizerId">The verified <c>sub</c> claim. Never read from a request body.</param>
/// <param name="IsAdmin">Admin bypasses the ownership check — including for legacy unowned events.</param>
public readonly record struct CatalogCaller(Guid OrganizerId, bool IsAdmin);

/// <summary>
/// Raised when a caller holds the organizer role but does not own the event being mutated.
/// </summary>
/// <remarks>
/// Surfaces as <c>403</c>, not <c>404</c>. Ticketing (Commit 10) deliberately chose 404 for the
/// mirror case, because there a 403 would confirm that someone else's order exists. That reasoning
/// does not transfer here: <c>GET /events</c> and <c>GET /events/{id}</c> are anonymous, so the
/// event's existence is already public and hiding it behind a 404 would only mislead the caller
/// about why the write failed while concealing nothing.
/// </remarks>
public sealed class EventOwnershipException(Guid eventId)
    : Exception($"Event '{eventId}' belongs to a different organizer.");
