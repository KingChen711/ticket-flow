using StackExchange.Redis;
using Testcontainers.Redis;

namespace TicketFlow.Reservation.IntegrationTests;

/// <summary>
/// One real Redis container shared across every test in a class (via IClassFixture) — starting
/// a fresh container per [Fact] would work too but be needlessly slow. Tests stay isolated from
/// each other by using a fresh Guid eventId/seatId per test, not by resetting the container.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7").Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}
