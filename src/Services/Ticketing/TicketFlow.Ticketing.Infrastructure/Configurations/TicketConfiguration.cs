using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure.Configurations;

public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.OrderId).IsRequired();
        builder.Property(t => t.SeatId).IsRequired();
        builder.Property(t => t.IssuanceState).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.RevocationReason).HasMaxLength(500);
        builder.HasIndex(t => new { t.OrderId, t.SeatId }).IsUnique();
        builder.HasIndex(t => t.CustomerId);
        builder.HasIndex(t => t.EventId);
        builder.Property(t => t.QrPayload).IsRequired().HasMaxLength(500);
        builder.HasIndex(t => t.QrPayload).IsUnique();
        builder.Property(t => t.PdfObjectKey).IsRequired().HasMaxLength(500);
    }
}
