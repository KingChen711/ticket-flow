using MassTransit;
using TicketFlow.Contracts.Messaging.Reservation;
using ApplicationReservationService = TicketFlow.Reservation.Application.Seats.ReservationService;

namespace TicketFlow.Reservation.Api.Consumers;

/// <summary>
/// Compensation step (Commit 9: PaymentFailed direct, or TicketingFailed after refund).
/// ReleaseAsync is already idempotent (Phase 2 doc comment on ReleaseSeats): releasing an
/// unknown/already-released hold is a no-op. But by the time PurchaseSaga sends ReleaseSeats,
/// ConfirmSeats has ALWAYS already run (it's the step before ProcessPayment/IssueTickets in the
/// happy path) — the seats are Sold, not merely Held, so ReleaseAsync alone (which only ever
/// operates on an active Redis hold) is a no-op that does NOT actually free the seats. Real gap
/// found while implementing Commit 9 — calls both: ReleaseAsync for the (here, hypothetical)
/// still-held case, UnsellAsync for the actual confirmed-sale case. Both are idempotent no-ops
/// when they don't apply, so calling both unconditionally is simpler and safe.
/// </summary>
public sealed class ReleaseSeatsConsumer(ApplicationReservationService reservationService, TimeProvider timeProvider)
    : IConsumer<ReleaseSeats>
{
    public async Task Consume(ConsumeContext<ReleaseSeats> context)
    {
        var message = context.Message;
        await reservationService.ReleaseAsync(message.EventId, message.HoldId, context.CancellationToken);
        await reservationService.UnsellAsync(message.EventId, message.SeatIds, context.CancellationToken);

        await context.Publish(new SeatsReleased(message.OrderId, message.HoldId, timeProvider.GetUtcNow()));
    }
}
