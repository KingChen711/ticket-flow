using Microsoft.AspNetCore.SignalR;
using TicketFlow.Reservation.Application.Abstractions;

namespace TicketFlow.Reservation.Api.Hubs;

public sealed class SignalRSeatMapNotifier(IHubContext<SeatMapHub> hubContext) : ISeatMapNotifier
{
    public Task SeatsUpdatedAsync(Guid eventId, IReadOnlyList<Guid> seatIds, string status, CancellationToken cancellationToken = default) =>
        SeatsUpdatedAsync(eventId, seatIds, status, generation: 0, DateTimeOffset.UnixEpoch, cancellationToken);

    public Task SeatsUpdatedAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        long generation,
        CancellationToken cancellationToken = default) =>
        SeatsUpdatedAsync(eventId, seatIds, status, generation, DateTimeOffset.UnixEpoch, cancellationToken);

    public Task SeatsUpdatedAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        long generation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        hubContext.Clients
            .Group(SeatMapHub.GroupName(eventId))
            .SendAsync("seatsUpdated", new { seatIds, status, generation, occurredAt }, cancellationToken);
}
