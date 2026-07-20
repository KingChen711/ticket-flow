using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Infrastructure.Configurations;

public sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.OrderId).IsRequired();
        builder.Property(a => a.OperationKey).IsRequired().HasMaxLength(200);
        builder.Property(a => a.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.GatewayReference).HasMaxLength(200);
        builder.Property(a => a.Amount).HasColumnType("numeric(12,2)").IsRequired();
        builder.Property(a => a.Currency).HasMaxLength(3).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.HasIndex(a => a.OperationKey).IsUnique();
        builder.HasIndex(a => a.OrderId);
    }
}
