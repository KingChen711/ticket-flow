using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.SeatMaps;

/// <summary>
/// Owned by a single Event, not by Venue — the same physical venue can be laid out
/// completely differently from one show to the next (stage placement, GA floor vs.
/// seated floor, catwalks, etc.), so the seat configuration cannot be a fixed
/// property of the venue itself.
/// </summary>
public sealed class SeatMap : AggregateRoot<Guid>
{
    private readonly List<Section> _sections = [];

    public Guid EventId { get; private set; }
    public IReadOnlyList<Section> Sections => _sections.AsReadOnly();

    private SeatMap(Guid id, Guid eventId) : base(id) => EventId = eventId;

    public static SeatMap CreateForEvent(Guid eventId)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), eventId);
        seatMap.Raise(new SeatMapCreated(seatMap.Id, eventId));
        return seatMap;
    }

    public void AddSection(string name, int capacity)
    {
        if (_sections.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Section '{name}' already exists in this seat map.");

        _sections.Add(Section.Create(name, capacity));
    }
}
