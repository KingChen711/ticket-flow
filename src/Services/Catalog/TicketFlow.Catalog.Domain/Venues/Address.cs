using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Venues;

public sealed class Address : ValueObject
{
    public string Street { get; }
    public string City { get; }
    public string Country { get; }
    public string? PostalCode { get; }

    private Address(string street, string city, string country, string? postalCode)
    {
        Street = street;
        City = city;
        Country = country;
        PostalCode = postalCode;
    }

    public static Address Create(string street, string city, string country, string? postalCode = null) =>
        new(
            Guard.NotNullOrEmpty(street, nameof(street)),
            Guard.NotNullOrEmpty(city, nameof(city)),
            Guard.NotNullOrEmpty(country, nameof(country)),
            postalCode);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Street;
        yield return City;
        yield return Country;
        yield return PostalCode;
    }
}
