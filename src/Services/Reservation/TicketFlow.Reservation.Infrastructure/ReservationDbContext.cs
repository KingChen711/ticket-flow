using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Domain.Allocations;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.SalePolicies;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class ReservationDbContext(DbContextOptions<ReservationDbContext> options) : DbContext(options)
{
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<SeatAllocation> SeatAllocations => Set<SeatAllocation>();
    public DbSet<ReservationOperationReceipt> ReservationOperationReceipts => Set<ReservationOperationReceipt>();
    public DbSet<EventSalePolicy> EventSalePolicies => Set<EventSalePolicy>();
    public DbSet<HoldQuote> HoldQuotes => Set<HoldQuote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("reservation");
        modelBuilder.HasSequence<long>("seat_allocation_generation");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReservationDbContext).Assembly);

        // MassTransit EF Core Inbox/Outbox. InboxState deduplicates redelivery of the same
        // physical message; the business operation receipt remains the semantic idempotency
        // boundary for distinct envelopes carrying the same operation key.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
