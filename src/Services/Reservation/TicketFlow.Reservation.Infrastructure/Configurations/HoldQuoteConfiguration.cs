using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Reservation.Domain.Holds;

namespace TicketFlow.Reservation.Infrastructure.Configurations;

public sealed class HoldQuoteConfiguration : IEntityTypeConfiguration<HoldQuote>
{
    public void Configure(EntityTypeBuilder<HoldQuote> builder)
    {
        builder.ToTable("hold_quotes");
        builder.HasKey(quote => quote.Id);
        builder.Property(quote => quote.Id).ValueGeneratedNever();
        builder.Property(quote => quote.EventId).IsRequired();
        builder.Property(quote => quote.OwnerUserId).IsRequired();
        builder.Property(quote => quote.SeatIds).HasColumnType("uuid[]").IsRequired();
        builder.Property(quote => quote.Amount).HasColumnType("numeric(12,2)").IsRequired();
        builder.Property(quote => quote.Currency).HasMaxLength(3).IsRequired();
        builder.Property(quote => quote.PriceSnapshotVersion).IsRequired();
        builder.HasIndex(quote => quote.OwnerUserId);
        builder.HasIndex(quote => quote.EventId);
    }
}
