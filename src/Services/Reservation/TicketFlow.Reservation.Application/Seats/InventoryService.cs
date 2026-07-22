using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Seats;

public sealed class InventoryService(
    ISeatRepository seatRepository,
    ISeatHoldStore holdStore,
    IUnitOfWork unitOfWork,
    ReservationOptions options)
{
    /// <summary>
    /// Idempotent provisioning (Commit 5). The pre-Phase-12 version threw
    /// "already provisioned" on the second call and loaded Redis only after a successful insert,
    /// so a crash between the PostgreSQL commit and the Redis load left the event permanently
    /// unusable: PostgreSQL insisted inventory existed, Redis had no seat set to validate holds
    /// against, and every retry was rejected before it could repair anything. That exact
    /// dead-end was hit for real during Phase 10 verification.
    ///
    /// Now the durable rows are the authority and the cache is repaired to match on every call:
    /// a retry after any failpoint converges instead of failing, and two concurrent provisioners
    /// resolve through the unique <c>(EventId, Label)</c> index rather than a check-then-insert
    /// race, both returning the same seat count.
    /// </summary>
    public async Task<int> ProvisionAsync(
        ProvisionInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var existing = await seatRepository.GetByEventAsync(request.EventId, cancellationToken);
        if (existing.Count > 0)
        {
            EnsureRequestMatchesExisting(request, existing);
            await LoadCacheAsync(request.EventId, existing.Select(seat => seat.Id).ToList(), cancellationToken);
            return existing.Count;
        }

        var seats = request.Sections
            .SelectMany(section =>
                SeatInventory.ForSection(request.EventId, section.SectionId, section.SectionName, section.Capacity))
            .ToList();

        seatRepository.AddRange(seats);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (IsUniqueViolation(exception))
        {
            // A concurrent provisioner won. Both callers must observe the same inventory rather
            // than one of them reporting a failure the operator would retry into a second attempt.
            var committed = await seatRepository.GetByEventAsync(request.EventId, cancellationToken);
            if (committed.Count == 0)
                throw;

            await LoadCacheAsync(request.EventId, committed.Select(seat => seat.Id).ToList(), cancellationToken);
            return committed.Count;
        }

        await LoadCacheAsync(request.EventId, seats.Select(seat => seat.Id).ToList(), cancellationToken);
        return seats.Count;
    }

    /// <summary>Cache load is a repair step, so it is always safe to repeat.</summary>
    private Task LoadCacheAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken) =>
        holdStore.LoadInventoryAsync(eventId, seatIds, cancellationToken);

    private void ValidateRequest(ProvisionInventoryRequest request)
    {
        if (request.EventId == Guid.Empty)
            throw new ArgumentException("Event id is required.", nameof(request));
        if (request.Sections.Count == 0)
            throw new ArgumentException("At least one section is required.", nameof(request));
        if (request.Sections.Count > options.MaxSectionsPerProvision)
            throw new ArgumentException(
                $"A provisioning request cannot contain more than {options.MaxSectionsPerProvision} sections.",
                nameof(request));
        if (request.Sections.Select(section => section.SectionId).Distinct().Count() != request.Sections.Count)
            throw new ArgumentException("Section ids must be unique.", nameof(request));
        if (request.Sections.Any(section => section.SectionId == Guid.Empty))
            throw new ArgumentException("Section id is required.", nameof(request));
        if (request.Sections.Any(section => string.IsNullOrWhiteSpace(section.SectionName)))
            throw new ArgumentException("Section name is required.", nameof(request));
        if (request.Sections.Select(section => section.SectionName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Sections.Count)
        {
            // Seat labels are "{SectionName}-{n}", so duplicate names would collide on the unique
            // (EventId, Label) index halfway through the insert.
            throw new ArgumentException("Section names must be unique within an event.", nameof(request));
        }

        foreach (var section in request.Sections)
        {
            if (section.Capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(request), "Section capacity must be positive.");
            if (section.Capacity > options.MaxSeatsPerSection)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"Section capacity cannot exceed {options.MaxSeatsPerSection}.");
            }
        }

        var totalSeats = request.Sections.Sum(section => section.Capacity);
        if (totalSeats > options.MaxSeatsPerProvision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"A provisioning request cannot allocate more than {options.MaxSeatsPerProvision} seats.");
        }
    }

    /// <summary>
    /// A repeat call must be the same request. Silently accepting a different section/capacity
    /// shape would report success while the durable inventory says something else entirely.
    /// </summary>
    private static void EnsureRequestMatchesExisting(
        ProvisionInventoryRequest request,
        IReadOnlyList<Seat> existing)
    {
        var requested = request.Sections
            .ToDictionary(section => section.SectionId, section => section.Capacity);
        var actual = existing
            .GroupBy(seat => seat.SectionId)
            .ToDictionary(group => group.Key, group => group.Count());

        if (requested.Count != actual.Count ||
            requested.Any(section =>
                !actual.TryGetValue(section.Key, out var capacity) || capacity != section.Value))
        {
            throw new InvalidOperationException(
                $"Inventory for event '{request.EventId}' already exists with a different section/capacity shape.");
        }
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // Matched on the PostgreSQL SQLSTATE rather than a Npgsql type so the Application
            // layer keeps no provider reference.
            if (current.GetType().GetProperty("SqlState")?.GetValue(current) as string == "23505")
                return true;
        }

        return false;
    }
}
