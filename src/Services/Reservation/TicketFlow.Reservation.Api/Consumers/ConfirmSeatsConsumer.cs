using MassTransit;
using TicketFlow.Contracts.Messaging.Reservation;
using ApplicationReservationService = TicketFlow.Reservation.Application.Seats.ReservationService;

namespace TicketFlow.Reservation.Api.Consumers;

/// <summary>
/// Calls ReservationService.ConfirmAsync (Phase 2's atomic Redis Lua script, unchanged — no
/// rewrite here) and translates its result into the saga's vocabulary. No Load-then-guard step
/// like Ordering's consumers: ConfirmAsync already IS the atomic, idempotent-by-construction
/// operation (a hold that's already confirmed or expired just returns empty).
/// </summary>
public sealed class ConfirmSeatsConsumer(ApplicationReservationService reservationService, TimeProvider timeProvider)
    : IConsumer<ConfirmSeats>
{
    public async Task Consume(ConsumeContext<ConfirmSeats> context)
    {
        var message = context.Message;
        var confirmedSeatIds = await reservationService.ConfirmAsync(message.EventId, message.HoldId, context.CancellationToken);

        if (confirmedSeatIds.Count > 0)
        {
            await context.Publish(new SeatsConfirmed(message.OrderId, confirmedSeatIds, timeProvider.GetUtcNow()));
        }
        else
        {
            await context.Publish(new SeatsConfirmationFailed(
                message.OrderId,
                message.HoldId,
                "Hold not found, already confirmed, or expired before checkout completed."));
        }
    }
}
