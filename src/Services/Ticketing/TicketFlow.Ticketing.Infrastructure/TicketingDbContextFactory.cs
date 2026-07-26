using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketFlow.Ticketing.Infrastructure;

/// <summary>EF tooling must not start Lambda/MinIO dependencies to generate a migration.</summary>
public sealed class TicketingDbContextFactory : IDesignTimeDbContextFactory<TicketingDbContext>
{
    public TicketingDbContext CreateDbContext(string[] args) => Create(
        "Host=localhost;Database=ticketflow_design;Username=postgres;Password=postgres");

    public static TicketingDbContext Create(string connectionString) => new(
        new DbContextOptionsBuilder<TicketingDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
}
