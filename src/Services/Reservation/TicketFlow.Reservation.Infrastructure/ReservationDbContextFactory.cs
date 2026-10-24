using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketFlow.Reservation.Infrastructure;

/// <summary>EF tooling must not start Redis/API infrastructure just to generate a migration.</summary>
public sealed class ReservationDbContextFactory : IDesignTimeDbContextFactory<ReservationDbContext>
{
    public ReservationDbContext CreateDbContext(string[] args) => Create(
        "Host=localhost;Database=ticketflow_design;Username=postgres;Password=postgres");

    public static ReservationDbContext Create(string connectionString) => new(
        new DbContextOptionsBuilder<ReservationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
}
