using MassTransit;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Reservation.Application.Abstractions;

namespace TicketFlow.Reservation.Api.Consumers;

/// <summary>
/// Relays a durable seat-availability notification to SignalR. This consumer deliberately runs
/// on its own queue and outside the allocation transaction: a hub outage retries this projection
/// without rolling back or redelivering the correctness-critical reservation mutation.
/// </summary>
public sealed class SeatMapNotificationConsumer(ISeatMapNotifier notifier)
    : IConsumer<SeatAvailabilityChanged>
{
    public Task Consume(ConsumeContext<SeatAvailabilityChanged> context)
    {
        var message = context.Message;
        return notifier.SeatsUpdatedAsync(
            message.EventId,
            message.SeatIds,
            message.Status,
            message.Generation,
            message.OccurredAt,
            context.CancellationToken);
    }
}
