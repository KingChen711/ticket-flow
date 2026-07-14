using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Infrastructure.Configurations;

public sealed class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.ToTable("payment_transactions");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.OrderId).IsRequired();
        builder.HasIndex(p => p.OrderId).IsUnique();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.GatewayRef).HasMaxLength(200);
        builder.Property(p => p.FailureReason).HasMaxLength(500);

        builder.OwnsOne(p => p.Amount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("amount").HasColumnType("numeric(12,2)").IsRequired();
            money.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        });
        builder.Navigation(p => p.Amount).IsRequired();

        // No Ignore(DomainEvents) needed — PaymentTransaction extends Entity<Guid>, not
        // AggregateRoot<Guid>, so it has no DomainEvents property (same non-issue as Seat,
        // Phase 2).
    }
}
