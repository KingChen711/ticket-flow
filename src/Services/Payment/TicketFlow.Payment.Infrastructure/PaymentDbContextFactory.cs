using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketFlow.Payment.Infrastructure;

/// <summary>EF tooling must not start gateway, messaging, or observability infrastructure.</summary>
public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql("Host=localhost;Database=ticketflow_design;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}
