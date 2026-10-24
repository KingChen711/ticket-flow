using MassTransit;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Reservation.Application.Abstractions;

namespace TicketFlow.Reservation.Api.Messaging;

/// <summary>
/// Writes seat-map notification intents through MassTransit's scoped publish endpoint. With the
/// Reservation bus outbox enabled, the message is stored beside the allocation transaction and
/// delivered to the non-critical SignalR relay only after commit.
/// </summary>
public sealed class MassTransitSeatAvailabilityEventPublisher(IPublishEndpoint publishEndpoint)
    : ISeatAvailabilityEventPublisher
{
    public Task PublishAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        long generation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        publishEndpoint.Publish(
            new SeatAvailabilityChanged(eventId, seatIds, status, generation, occurredAt),
            cancellationToken);
}
