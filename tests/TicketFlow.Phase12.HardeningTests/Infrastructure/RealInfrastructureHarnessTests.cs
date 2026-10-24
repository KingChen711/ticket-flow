using Marten;
using StackExchange.Redis;
using TicketFlow.Phase12.HardeningTests.Support;

namespace TicketFlow.Phase12.HardeningTests.Infrastructure;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class RealInfrastructureHarnessTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Postgres_Marten_Redis_and_RabbitMq_are_real_and_round_trip()
    {
        var probeId = Guid.NewGuid();

        await using (var session = environment.MartenStore.LightweightSession())
        {
            session.Store(new MartenHarnessProbe { Id = probeId, Value = "marten-ready" });
            await session.SaveChangesAsync();
        }

        await using (var query = environment.MartenStore.QuerySession())
        {
            var persisted = await query.LoadAsync<MartenHarnessProbe>(probeId);
            Assert.Equal("marten-ready", persisted?.Value);
        }

        await using (var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString))
        {
            var database = redis.GetDatabase();
            await database.StringSetAsync($"phase12:probe:{probeId:N}", "redis-ready", TimeSpan.FromMinutes(1));
            Assert.Equal("redis-ready", (string?)await database.StringGetAsync($"phase12:probe:{probeId:N}"));
        }

        await environment.PublishEndpoint.Publish(new InfrastructureProbeMessage(probeId, "rabbit-ready"));
        var consumed = await AsyncTest.WaitUntilAsync(
            () => environment.Messages.Count<InfrastructureProbeMessage>(message => message.Id == probeId) == 1,
            TimeSpan.FromSeconds(10));

        Assert.True(consumed, "The real RabbitMQ consumer did not receive the infrastructure probe.");
    }
}
