using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure;

public sealed class TicketingDbContext(DbContextOptions<TicketingDbContext> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketIssuanceJob> IssuanceJobs => Set<TicketIssuanceJob>();

    public DbSet<GateAssignment> GateAssignments => Set<GateAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ticketing");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TicketingDbContext).Assembly);

        // MassTransit EF Core Inbox/Outbox (Commit 8) — same pattern as Payment's Commit 7.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
