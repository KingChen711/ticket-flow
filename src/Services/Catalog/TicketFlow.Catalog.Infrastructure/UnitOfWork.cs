using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketFlow.Catalog.Application.Abstractions;

namespace TicketFlow.Catalog.Infrastructure;

public sealed class UnitOfWork(CatalogDbContext dbContext) : IUnitOfWork
{
    public void DiscardChanges() => dbContext.ChangeTracker.Clear();

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Translated at the port boundary (Commit 11) so Application depends on
            // ConcurrencyConflictException, not on this EF type.
            throw new ConcurrencyConflictException(
                "This event was changed by another request. Reload it and try again.", exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Same conflict shape, different guard: two concurrent AddSection/AddPriceTier calls
            // for two different names/sections can each miss the other's not-yet-saved row and
            // both pass the in-memory duplicate check (EventConfiguration/SeatMapConfiguration's
            // doc comments). The unique index is what actually stops it; this is the loser
            // finding out.
            throw new ConcurrencyConflictException(
                "This conflicts with a section or price tier added by another request. Reload and try again.",
                exception);
        }
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        }

        return false;
    }
}
