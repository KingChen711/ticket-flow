using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Seats;

public sealed class InventoryService(
    ISeatRepository seatRepository,
    ISeatHoldStore holdStore,
    IUnitOfWork unitOfWork)
{
    /// <summary>
    /// Self-provision (Phase 2 has no message bus yet). Postgres first (durable truth), then
    /// load the seat ids into Redis so holds can validate against them. Event-driven
    /// provisioning (catalog EventPublished → provision) is Phase 3.
    /// </summary>
    public async Task<int> ProvisionAsync(ProvisionInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await seatRepository.InventoryExistsAsync(request.EventId, cancellationToken))
            throw new InvalidOperationException(
                $"Inventory for event '{request.EventId}' has already been provisioned.");

        var seats = request.Sections
            .SelectMany(section =>
                SeatInventory.ForSection(request.EventId, section.SectionId, section.SectionName, section.Capacity))
            .ToList();

        seatRepository.AddRange(seats);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        //TODO: What wrong if the app crash before run the code below? Will the reliability fail?
        await holdStore.LoadInventoryAsync(request.EventId, seats.Select(seat => seat.Id).ToList(), cancellationToken);

        return seats.Count;
    }
}
