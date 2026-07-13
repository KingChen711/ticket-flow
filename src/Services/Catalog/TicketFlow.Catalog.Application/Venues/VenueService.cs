using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Venues;

namespace TicketFlow.Catalog.Application.Venues;

public sealed class VenueService(IVenueRepository venueRepository, IUnitOfWork unitOfWork)
{
    public async Task<VenueResponse> RegisterAsync(RegisterVenueRequest request, CancellationToken cancellationToken = default)
    {
        var address = Address.Create(request.Street, request.City, request.Country, request.PostalCode);
        var venue = Venue.Register(request.Name, address);

        venueRepository.Add(venue);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(venue);
    }

    public async Task<VenueResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var venue = await venueRepository.GetByIdAsync(id, cancellationToken);
        return venue is null ? null : Map(venue);
    }

    public async Task<IReadOnlyList<VenueResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var venues = await venueRepository.ListAsync(cancellationToken);
        return venues.Select(Map).ToList();
    }

    private static VenueResponse Map(Venue venue) => new(
        venue.Id,
        venue.Name,
        venue.Address.Street,
        venue.Address.City,
        venue.Address.Country,
        venue.Address.PostalCode);
}
