using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Catalog.Domain.SeatMaps;

namespace TicketFlow.Catalog.Infrastructure.Configurations;

public sealed class SeatMapConfiguration : IEntityTypeConfiguration<SeatMap>
{
    public void Configure(EntityTypeBuilder<SeatMap> builder)
    {
        builder.ToTable("seat_maps");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.EventId).IsRequired();

        // Enforces "1 SeatMap per Event" at the DB level too, mirroring the check in SeatMapService.
        builder.HasIndex(s => s.EventId).IsUnique();

        builder.OwnsMany(s => s.Sections, section =>
        {
            section.ToTable("seat_map_sections");
            section.WithOwner().HasForeignKey("SeatMapId");
            section.HasKey(sec => sec.Id);
            section.Property(sec => sec.Id).ValueGeneratedNever();
            section.Property(sec => sec.Name).IsRequired().HasMaxLength(150);
            section.Property(sec => sec.Capacity).IsRequired();
        });
        builder.Navigation(s => s.Sections).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(s => s.DomainEvents);
    }
}
