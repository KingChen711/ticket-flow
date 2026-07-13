using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure.Configurations;

public sealed class SeatConfiguration : IEntityTypeConfiguration<Seat>
{
    public void Configure(EntityTypeBuilder<Seat> builder)
    {
        builder.ToTable("seats");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.EventId).IsRequired();
        builder.Property(s => s.SectionId).IsRequired();
        builder.Property(s => s.Label).IsRequired().HasMaxLength(50);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);

        // Fast "all seats for this event/section" lookups (GetByEventAsync, provisioning checks).
        builder.HasIndex(s => new { s.EventId, s.SectionId });

        // A label like "VIP-12" must be unique within its event — guards against provisioning
        // the same event twice generating duplicate seats.
        builder.HasIndex(s => new { s.EventId, s.Label }).IsUnique();

        // Note: Seat is a plain Entity<Guid>, not an AggregateRoot<Guid> — it has no
        // DomainEvents property, so there's nothing to Ignore() here (unlike Catalog/Identity).
    }
}
