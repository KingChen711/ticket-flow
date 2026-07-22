namespace TicketFlow.Reservation.Application.Abstractions;

// Port only — SignalR/IHubContext are ASP.NET Core specifics that belong in Api (same reasoning
// as ISeatHoldStore/ISeatRepository living here as ports, Infrastructure/Api owning the concrete
// tech). Real-time seat map (Phase 8 Commit 6, docs/phase-8-plan.md mục 5).
public interface ISeatMapNotifier
{
    Task SeatsUpdatedAsync(Guid eventId, IReadOnlyList<Guid> seatIds, string status, CancellationToken cancellationToken = default);

    // Backward-compatible overload for durable, versioned notifications. Existing test fakes and
    // pre-Phase-12 callers can keep implementing/calling the original method; production SignalR
    // implements this overload so clients receive the allocation generation.
    Task SeatsUpdatedAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        long generation,
        CancellationToken cancellationToken = default) =>
        SeatsUpdatedAsync(eventId, seatIds, status, cancellationToken);
}
