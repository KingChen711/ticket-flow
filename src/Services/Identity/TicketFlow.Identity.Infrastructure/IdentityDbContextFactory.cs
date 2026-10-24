using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketFlow.Identity.Infrastructure;

/// <summary>EF tooling must not start Keycloak, observability, or the Identity HTTP host.</summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => Create(
        "Host=localhost;Database=ticketflow_design;Username=postgres;Password=postgres");

    public static IdentityDbContext Create(string connectionString) => new(
        new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
}
