using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketFlow.Catalog.Infrastructure;

/// <summary>EF tooling must not start HTTP, RabbitMQ, or API infrastructure to generate a migration.</summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) => Create(
        "Host=localhost;Database=ticketflow_design;Username=postgres;Password=postgres");

    public static CatalogDbContext Create(string connectionString) => new(
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
}
