using Microsoft.AspNetCore.SignalR;

namespace TicketFlow.Reservation.Api.Hubs;

// One group per event — clients viewing a specific event's seat map join it, broadcasts from
// ReservationService (hold/release/confirm) only reach viewers of that event.
public sealed class SeatMapHub : Hub
{
    public Task JoinEvent(Guid eventId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupName(eventId));

    public Task LeaveEvent(Guid eventId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(eventId));

    public static string GroupName(Guid eventId) => $"event:{eventId}";
}
