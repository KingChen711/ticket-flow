using MassTransit;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Reservation.Application.Seats;
using ApplicationReservationService = TicketFlow.Reservation.Application.Seats.ReservationService;

namespace TicketFlow.Reservation.Api.Consumers;

/// <summary>
/// The only external confirmation entry point. Every successful delivery goes through the
/// authority-aware quote-to-allocation transition and durable operation receipt.
/// </summary>
public sealed class ConfirmSeatsConsumer(ApplicationReservationService reservationService)
    : IConsumer<ConfirmSeats>
{
    public async Task Consume(ConsumeContext<ConfirmSeats> context)
    {
        var message = context.Message;
        ContractVersionMetrics.RecordInbound(nameof(ConfirmSeats), message.PriceSnapshotVersion);
        if (message.OwnerUserId is null || message.Amount is null || message.Amount <= 0 ||
            string.IsNullOrWhiteSpace(message.Currency) || message.Generation <= 0 ||
            message.PriceSnapshotVersion <= 0 || message.SeatIds.Count == 0)
        {
            await context.Publish(new SeatsConfirmationFailed(
                message.OrderId,
                message.HoldId,
                "ConfirmSeats is missing authoritative quote, price, seat-set, or generation data.",
                message.OperationKey,
                message.Generation));
            return;
        }

        var result = await reservationService.ConfirmAsync(
            new ConfirmSeatAllocationRequest(
                message.OrderId,
                message.EventId,
                message.HoldId,
                message.SeatIds,
                message.OwnerUserId.Value,
                message.Generation,
                message.PriceSnapshotVersion,
                message.Amount.Value,
                message.Currency,
                message.OperationKey),
            context.CancellationToken);

        if (result.Succeeded)
        {
            await context.Publish(new SeatsConfirmed(
                result.OrderId,
                result.SeatIds,
                result.DecidedAt,
                result.AllocationReference.ToString("N"),
                message.OperationKey,
                AggregateVersion: 0,
                Generation: result.Generation));
        }
        else
        {
            await context.Publish(new SeatsConfirmationFailed(
                result.OrderId,
                result.HoldId,
                result.FailureReason ?? "The hold could not be confirmed.",
                message.OperationKey,
                result.Generation));
        }
    }
}
