using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Reservation.Domain.Holds;

namespace TicketFlow.Reservation.Infrastructure.Configurations;

public sealed class HoldQuoteConfiguration : IEntityTypeConfiguration<HoldQuote>
{
    public void Configure(EntityTypeBuilder<HoldQuote> builder)
    {
        builder.ToTable("hold_quotes", table => table.HasCheckConstraint(
            "ck_hold_quotes_lifecycle",
            "(state = 'Active' AND order_id IS NULL AND terminal_at IS NULL) OR " +
            "(state = 'Consumed' AND order_id IS NOT NULL AND terminal_at IS NOT NULL) OR " +
            "(state IN ('Expired', 'Invalidated') AND order_id IS NULL AND terminal_at IS NOT NULL)"));
        builder.HasKey(quote => quote.Id);
        builder.Property(quote => quote.Id).ValueGeneratedNever();
        builder.Property(quote => quote.EventId).IsRequired();
        builder.Property(quote => quote.OwnerUserId).IsRequired();
        builder.Property(quote => quote.SeatIds).HasColumnType("uuid[]").IsRequired();
        builder.Property(quote => quote.Amount).HasColumnType("numeric(12,2)").IsRequired();
        builder.Property(quote => quote.Currency).HasMaxLength(3).IsRequired();
        builder.Property(quote => quote.PriceSnapshotVersion).IsRequired();
        builder.Property(quote => quote.Generation).IsRequired();
        builder.Property(quote => quote.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasIndex(quote => quote.OwnerUserId);
        builder.HasIndex(quote => quote.EventId);
        builder.HasIndex(quote => new { quote.EventId, quote.State });
        builder.HasIndex(quote => new { quote.OwnerUserId, quote.State });
        builder.HasIndex(quote => quote.OrderId)
            .IsUnique()
            .HasFilter("order_id IS NOT NULL");
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
