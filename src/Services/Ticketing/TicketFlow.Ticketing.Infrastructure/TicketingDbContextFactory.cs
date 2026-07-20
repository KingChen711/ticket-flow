using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketFlow.Ticketing.Infrastructure;

/// <summary>EF tooling must not start Lambda/MinIO dependencies to generate a migration.</summary>
public sealed class TicketingDbContextFactory : IDesignTimeDbContextFactory<TicketingDbContext>
{
    public TicketingDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<TicketingDbContext>()
            .UseNpgsql("Host=localhost;Database=ticketflow_design;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}
