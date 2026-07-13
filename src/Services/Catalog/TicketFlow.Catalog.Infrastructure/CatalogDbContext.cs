using Microsoft.EntityFrameworkCore;
using TicketFlow.Catalog.Domain.Events;
using TicketFlow.Catalog.Domain.SeatMaps;
using TicketFlow.Catalog.Domain.Venues;

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
    }
}
