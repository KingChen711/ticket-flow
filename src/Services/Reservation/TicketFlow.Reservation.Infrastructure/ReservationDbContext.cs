using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Domain.Allocations;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class ReservationDbContext(DbContextOptions<ReservationDbContext> options) : DbContext(options)
{
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<SeatAllocation> SeatAllocations => Set<SeatAllocation>();
    public DbSet<ReservationOperationReceipt> ReservationOperationReceipts => Set<ReservationOperationReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("reservation");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReservationDbContext).Assembly);
    }
}
