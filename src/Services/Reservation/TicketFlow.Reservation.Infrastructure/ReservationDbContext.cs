using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class ReservationDbContext(DbContextOptions<ReservationDbContext> options) : DbContext(options)
{
    public DbSet<Seat> Seats => Set<Seat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("reservation");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReservationDbContext).Assembly);
    }
}
