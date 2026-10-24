using System.Data;
using Microsoft.EntityFrameworkCore;
using TicketFlow.Reservation.Application.Abstractions;

namespace TicketFlow.Reservation.Infrastructure;

public sealed class PostgresAllocationGenerationSource(ReservationDbContext dbContext)
    : IAllocationGenerationSource
{
    public async Task<long> NextAsync(CancellationToken cancellationToken = default)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT nextval('reservation.seat_allocation_generation')";
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(value);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }
}
