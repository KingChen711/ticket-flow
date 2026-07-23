namespace TicketFlow.Ordering.Application.Sagas;

/// <summary>
/// The saga's own deadline message (Commit 9). Scheduled when the saga starts waiting on a
/// participant; if it ever arrives, that participant went silent.
/// </summary>
/// <param name="Token">
/// Identifies the exact wait this deadline was armed for. The saga keeps the token of its current
/// wait, so a deadline whose token no longer matches is stale — the participant answered, or the
/// wait was re-armed — and is ignored rather than counted as silence.
///
/// A token is used instead of cancelling the scheduled message because cancellation is a race:
/// the message may already be in flight when the answer arrives. Recognising a stale deadline on
/// arrival is decidable; preventing its delivery is not.
/// </param>
public sealed record PurchaseDeadlineExpired(Guid OrderId, string ExpectedState, Guid Token);

/// <summary>
/// An operator asking the saga to try again (Commit 9). The only write operation the operator API
/// exposes, and deliberately the weakest one available: it resets the strike count and re-sends
/// whatever command the saga is already waiting on. It cannot assert an outcome — there is no
/// force-complete and no force-cancel — because a purchase's outcome must come from the
/// participants that actually did the work.
/// </summary>
public sealed record ResumePurchase(Guid OrderId, string RequestedBy);
