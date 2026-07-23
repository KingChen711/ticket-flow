using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketFlow.Identity.Application.Abstractions;

namespace TicketFlow.Identity.Infrastructure;

public sealed class UnitOfWork(IdentityDbContext dbContext) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            dbContext.ChangeTracker.Clear();
            throw new RegistrationPersistenceConflictException(
                "Registration was changed by another request; reload and continue from its checkpoint.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Covers both the stable registration key and normalized-email uniqueness. Clearing
            // is required: EF otherwise retries the losing INSERT on the next SaveChanges.
            dbContext.ChangeTracker.Clear();
            throw new RegistrationPersistenceConflictException(
                "Registration key or email was claimed by another request.",
                exception);
        }
    }

    public void DiscardChanges() => dbContext.ChangeTracker.Clear();

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
