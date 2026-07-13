using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Catalog.Domain.Venues;

namespace TicketFlow.Catalog.Infrastructure.Configurations;

public sealed class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        builder.ToTable("venues");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Name).IsRequired().HasMaxLength(200);

        builder.OwnsOne(v => v.Address, address =>
        {
            address.Property(a => a.Street).HasColumnName("address_street").IsRequired().HasMaxLength(300);
            address.Property(a => a.City).HasColumnName("address_city").IsRequired().HasMaxLength(150);
            address.Property(a => a.Country).HasColumnName("address_country").IsRequired().HasMaxLength(150);
            address.Property(a => a.PostalCode).HasColumnName("address_postal_code").HasMaxLength(20);
        });
        builder.Navigation(v => v.Address).IsRequired();

        builder.Ignore(v => v.DomainEvents);
    }
}
