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
        builder.Property(a => a.FailureReason).HasMaxLength(500);
        builder.Property(a => a.ReconcileAttempts).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.CancellationRequestedAt);
        builder.Property(a => a.CancellationWorkflowId);
        builder.Property(a => a.CancellationOperationKey).HasMaxLength(200);
        builder.Property(a => a.CancellationCompletedAt);
        builder.HasIndex(a => a.OperationKey).IsUnique();

        // Commit 6 makes this UNIQUE. It is the actual double-charge guard: the attempt row is
        // committed before the provider is called, so two concurrent consumers cannot both get
        // past the claim — the loser sees a unique violation and replays the winner's outcome
        // instead of charging again.
        builder.HasIndex(a => a.OrderId).IsUnique();
        builder.HasIndex(a => a.State);
    }
}
