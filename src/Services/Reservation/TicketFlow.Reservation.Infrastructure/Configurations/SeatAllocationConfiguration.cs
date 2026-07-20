using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Reservation.Domain.Allocations;

namespace TicketFlow.Reservation.Infrastructure.Configurations;

public sealed class SeatAllocationConfiguration : IEntityTypeConfiguration<SeatAllocation>
{
    public void Configure(EntityTypeBuilder<SeatAllocation> builder)
    {
        builder.ToTable("seat_allocations");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.SeatId).IsRequired();
        builder.Property(a => a.EventId).IsRequired();
        builder.Property(a => a.HoldId).IsRequired();
        builder.Property(a => a.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.Generation).IsRequired();
        builder.Property(a => a.Currency).HasMaxLength(3);
        builder.Property(a => a.Amount).HasColumnType("numeric(12,2)");
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();

        builder.HasIndex(a => new { a.EventId, a.SeatId }).IsUnique();
        builder.HasIndex(a => a.HoldId);
        builder.HasIndex(a => a.OrderId);
        builder.HasIndex(a => a.OwnerUserId);
        // PostgreSQL's system xmin supplies optimistic concurrency without an application column.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
