namespace TicketFlow.Catalog.Application.Abstractions;

/// <summary>
/// Raised when a save loses the optimistic-concurrency race — another request committed a change
/// to the same aggregate between this request's load and its save (Commit 11).
/// </summary>
/// <remarks>
/// Before this, two concurrent <c>Publish</c>/<c>Cancel</c>/<c>Reschedule</c> calls on one Event
/// both loaded the same row, both mutated their in-memory copy, and both saved — Postgres has no
/// opinion on that, so whichever request's UPDATE ran last simply overwrote the other's, silently.
/// An organizer cancelling an event and, in the same second, another organizer rescheduling it
/// could leave the aggregate Published with a stale schedule instead of Cancelled — last write
/// wins, with no signal that anything was lost.
///
/// The fix is EF's own <c>xmin</c>-backed concurrency token (already configured on
/// <c>EventConfiguration</c>) plus this translation at the port boundary: Application depends on
/// this type, not on <c>DbUpdateConcurrencyException</c>, so the EF dependency stays inside
/// Infrastructure. The right response is never "retry the same write blindly" — the loser's
/// decision may already be wrong given what the winner just did — so this surfaces as a 409 the
/// caller must reload and re-decide against, not a request the server retries on its own.
/// </remarks>
public sealed class ConcurrencyConflictException(string message, Exception? innerException = null)
    : Exception(message, innerException);
