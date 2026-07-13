namespace TicketFlow.Catalog.Domain.Venues;

public sealed class SeatMap
{
    private readonly List<Section> _sections = [];

    public IReadOnlyList<Section> Sections => _sections.AsReadOnly();

    internal void AddSection(string name, int capacity)
    {
        if (_sections.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Section '{name}' already exists in this seat map.");

        _sections.Add(Section.Create(name, capacity));
    }
}
