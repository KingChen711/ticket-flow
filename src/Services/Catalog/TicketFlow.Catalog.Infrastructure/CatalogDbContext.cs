using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketFlow.Catalog.Domain.SeatMaps;
using TicketFlow.Catalog.Domain.Venues;
using Event = TicketFlow.Catalog.Domain.Events.Event;

namespace TicketFlow.Catalog.Infrastructure;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<SeatMap> SeatMaps => Set<SeatMap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);

        // MassTransit EF Core Outbox — not auto-injected into an arbitrary DbContext's model,
        // must be added explicitly here. Lands in the "catalog" schema like everything else
        // (HasDefaultSchema above applies to entities with no explicit schema, order-independent).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
