using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Infrastructure.Configurations;

public sealed class PaymentRefundConfiguration : IEntityTypeConfiguration<PaymentRefund>
{
    public void Configure(EntityTypeBuilder<PaymentRefund> builder)
    {
        builder.ToTable("payment_refunds");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.PaymentId).IsRequired();
        builder.Property(r => r.OrderId).IsRequired();
        builder.Property(r => r.OperationKey).IsRequired().HasMaxLength(200);
        builder.Property(r => r.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.GatewayReference).HasMaxLength(200);
        builder.Property(r => r.Amount).HasColumnType("numeric(12,2)").IsRequired();
        builder.Property(r => r.Currency).HasMaxLength(3).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.HasIndex(r => r.OperationKey).IsUnique();
        builder.HasIndex(r => r.PaymentId);
    }
}
