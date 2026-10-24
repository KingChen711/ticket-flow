using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Reservation.Domain.Allocations;

namespace TicketFlow.Reservation.Infrastructure.Configurations;

internal sealed class ReservationOperationReceiptConfiguration
    : IEntityTypeConfiguration<ReservationOperationReceipt>
{
    public void Configure(EntityTypeBuilder<ReservationOperationReceipt> builder)
    {
        builder.ToTable("reservation_operation_receipts");

        builder.HasKey(receipt => receipt.Id);

        builder.Property(receipt => receipt.Id)
            .ValueGeneratedNever();

        builder.Property(receipt => receipt.OperationType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(receipt => receipt.BusinessKey)
            .IsRequired()
            .HasMaxLength(300);

        builder.Property(receipt => receipt.RequestFingerprint)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(receipt => receipt.Outcome)
            // JSON snapshots are kept as text during the expand window so the Commit-3 binary,
            // which maps this column as varchar, can still insert/read receipts after migration.
            .HasColumnType("text")
            .IsRequired(false);

        builder.Property(receipt => receipt.CreatedAt)
            .IsRequired();

        builder.Property(receipt => receipt.CompletedAt)
            .IsRequired(false);

        builder.HasIndex(receipt => new { receipt.OperationType, receipt.BusinessKey })
            .IsUnique();
    }
}
