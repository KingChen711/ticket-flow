using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Reservation.Application.Seats;
using ApplicationReservationService = TicketFlow.Reservation.Application.Seats.ReservationService;

namespace TicketFlow.Reservation.Api.Consumers;

/// <summary>
/// The only compensation entry point. Phase 12 Commit 5 replaced the previous
/// <c>ReleaseAsync + UnsellAsync</c> pair — which asserted nothing about ownership and therefore
/// freed whichever seat happened to carry the id — with one owner-scoped operation guarded by a
/// durable receipt.
/// </summary>
/// <remarks>
/// <c>SeatsReleased</c> is published even when the service could not release anything (stale
/// compensation, hold owned by another order, authority mismatch). That is deliberate: those
/// outcomes mean this order has nothing left to take back, and withholding the event would leave
/// the saga stuck in <c>CompensatingSeats</c> forever instead of reaching <c>Cancelled</c>. The
/// unreleased case is not silent — it increments
/// <c>ticketflow.reservation.compensation_total{outcome=stale_noop|conflict}</c> and logs a
/// warning. Turning those into an explicit ManualReview state belongs to Commit 9's saga
/// deadlines/recovery tooling, not here.
/// </remarks>
public sealed class ReleaseSeatsConsumer(
    ApplicationReservationService reservationService,
    TimeProvider timeProvider,
    ILogger<ReleaseSeatsConsumer> logger)
    : IConsumer<ReleaseSeats>
{
    public async Task Consume(ConsumeContext<ReleaseSeats> context)
    {
        var message = context.Message;
        ContractVersionMetrics.RecordInbound(nameof(ReleaseSeats), message.OperationKey is null ? 0 : 1);

        var result = await reservationService.CompensateAsync(
            new ReleaseSeatAllocationRequest(
                message.OrderId,
                message.EventId,
                message.HoldId,
                message.SeatIds,
                message.OwnerUserId,
                message.Generation,
                message.OperationKey),
            context.CancellationToken);

        if (!result.Succeeded || result.StaleSeatIds.Count > 0)
        {
            logger.LogWarning(
                "[reservation] compensation for order {OrderId} hold {HoldId} released {ReleasedCount} seat(s) " +
                "and refused {StaleCount} seat(s) owned by a newer allocation ({FailureCode}: {FailureReason}).",
                message.OrderId,
                message.HoldId,
                result.ReleasedSeatIds.Count,
                result.StaleSeatIds.Count,
                result.FailureCode ?? "None",
                result.FailureReason ?? "n/a");
        }

        await context.Publish(new SeatsReleased(
            message.OrderId,
            message.HoldId,
            timeProvider.GetUtcNow(),
            message.OperationKey));
    }
}
