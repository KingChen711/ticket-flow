using MassTransit;
using TicketFlow.Contracts.Messaging.Reservation;
using ApplicationReservationService = TicketFlow.Reservation.Application.Seats.ReservationService;

namespace TicketFlow.Reservation.Api.Consumers;

/// <summary>
/// Compensation step (used from Commit 9 onward once the saga has failure branches — this
/// consumer works from day one regardless). ReleaseAsync is already idempotent (Phase 2 doc
/// comment on ReleaseSeats): releasing an unknown/already-released hold is a no-op, not an
/// error. So SeatsReleased is published unconditionally after the call — both "released it just
/// now" and "was already released" mean the same thing to the saga: the seats are free.
/// </summary>
public sealed class ReleaseSeatsConsumer(ApplicationReservationService reservationService, TimeProvider timeProvider)
    : IConsumer<ReleaseSeats>
{
    public async Task Consume(ConsumeContext<ReleaseSeats> context)
    {
        var message = context.Message;
        await reservationService.ReleaseAsync(message.EventId, message.HoldId, context.CancellationToken);
        await context.Publish(new SeatsReleased(message.OrderId, message.HoldId, timeProvider.GetUtcNow()));
    }
}
