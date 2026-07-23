using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure.Configurations;

public sealed class TicketIssuanceJobConfiguration : IEntityTypeConfiguration<TicketIssuanceJob>
{
    public void Configure(EntityTypeBuilder<TicketIssuanceJob> builder)
    {
        builder.ToTable("ticket_issuance_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.OrderId).IsRequired();

        // The whole point of the row: one issuance per order, enforced by Postgres rather than by
        // a check the second caller can run before the first one has written anything.
        builder.HasIndex(j => j.OrderId).IsUnique();

        builder.Property(j => j.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(j => j.FailureReason).HasMaxLength(500);
        builder.HasIndex(j => j.State);
    }
}
