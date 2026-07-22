using TicketFlow.Contracts.Messaging.Ordering;

namespace TicketFlow.Ordering.Application.Checkout;

public enum OrderDispatchState
{
    Pending = 0,
    Dispatched = 1,
}

/// <summary>
/// The transactional outbox row for the one message that starts a purchase: <c>OrderSubmitted</c>.
/// Written in the same Marten transaction as the order stream, then relayed to the broker by
/// <c>OrderDispatchRelay</c>.
/// </summary>
/// <remarks>
/// Why a document and not a Marten async-daemon subscription over <c>OrderPlaced</c>, which the
/// old <c>PlaceOrderCommandHandler</c> comment predicted would be the idiomatic fix: the message
/// needs the payment method, the buyer's name and email, the idempotency key and the hold/quote
/// reference, and none of those belong in <c>OrderPlaced</c>. They are transport and notification
/// concerns, not order state — the very reason the original handler kept them out of the event. An
/// outbox row keeps the domain event clean and still gives the relay everything it needs.
///
/// Keyed by <c>OrderId</c>, so the row itself is the "exactly one submit per order" constraint;
/// there is no way to enqueue a second one.
/// </remarks>
public sealed class OrderDispatch
{
    public Guid Id { get; set; }

    public OrderDispatchState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// The message stored whole. Rebuilding it at relay time from scattered fields would let the
    /// relay publish something subtly different from what was decided at checkout.
    /// </summary>
    public OrderSubmitted Payload { get; set; } = null!;

    public static OrderDispatch Pending(OrderSubmitted payload, DateTimeOffset now) => new()
    {
        Id = payload.OrderId,
        State = OrderDispatchState.Pending,
        CreatedAt = now,
        Payload = payload,
    };

    public void MarkDispatched(DateTimeOffset now)
    {
        State = OrderDispatchState.Dispatched;
        DispatchedAt = now;
        LastError = null;
    }

    public void RecordFailure(string error)
    {
        Attempts++;
        LastError = error.Length > 500 ? error[..500] : error;
    }
}
