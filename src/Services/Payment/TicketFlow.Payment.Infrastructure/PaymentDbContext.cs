using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketFlow.Payment.Domain.Payments;

namespace TicketFlow.Payment.Infrastructure;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payment");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentDbContext).Assembly);

        // MassTransit EF Core Inbox/Outbox (Commit 7) — InboxState is what actually dedupes a
        // redelivered message (same MessageId) before ProcessPaymentConsumer/RefundPaymentConsumer
        // run again; OutboxMessage/OutboxState exist because UseEntityFrameworkOutbox needs both
        // sides of the same table set, even though these consumers don't publish inside the same
        // transaction as their own consume (same "add all three" pattern Catalog already uses).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
