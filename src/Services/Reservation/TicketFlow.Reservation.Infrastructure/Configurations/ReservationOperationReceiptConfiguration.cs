using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Reservation.Domain.Allocations;

namespace TicketFlow.Reservation.Infrastructure.Configurations;

public sealed class ReservationOperationReceiptConfiguration : IEntityTypeConfiguration<ReservationOperationReceipt>
{
    public void Configure(EntityTypeBuilder<ReservationOperationReceipt> builder)
    {
        builder.ToTable("reservation_operation_receipts");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.OperationType).IsRequired().HasMaxLength(100);
        builder.Property(r => r.BusinessKey).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Outcome).HasMaxLength(100);
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.HasIndex(r => new { r.OperationType, r.BusinessKey }).IsUnique();
    }
}
