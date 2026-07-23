using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketFlow.Identity.Infrastructure;

/// <summary>EF tooling must not start Keycloak, observability, or the Identity HTTP host.</summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=ticketflow_design;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}
