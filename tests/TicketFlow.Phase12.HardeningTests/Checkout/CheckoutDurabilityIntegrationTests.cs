using Marten;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Checkout;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;
using TicketFlow.Ordering.Infrastructure;
using TicketFlow.Phase12.HardeningTests.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Checkout;

/// <summary>
/// The in-memory checkout tests prove the service's decisions; these prove the guarantees those
/// decisions rest on are real. A unique index that only exists in a fake is not a constraint, and
/// "one transaction" is a claim about Postgres, not about C#.
/// </summary>
[Collection(HardeningEnvironmentCollection.Name)]
public sealed class CheckoutDurabilityIntegrationTests(HardeningEnvironmentFixture fixture) : IAsyncLifetime
{
    private IDocumentStore _store = null!;

    public Task InitializeAsync()
    {
        // The production configuration itself, not a copy of it.
        _store = DocumentStore.For(options =>
        {
            options.Connection(fixture.PostgresConnectionString);
            options.DatabaseSchemaName = $"phase12_checkout_{Guid.NewGuid():N}"[..40];
            ServiceCollectionExtensions.Configure(options);
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Two_checkouts_of_one_hold_must_be_refused_by_Postgres_not_just_by_the_service()
    {
        var holdId = Guid.NewGuid();
        await using (var session = _store.LightweightSession())
        {
            session.Insert(NewOperation("customer-a:key-1", holdId));
            await session.SaveChangesAsync();
        }

        // A different key, a different customer, the same hold — every application-level lookup
        // this could bypass, bypassed. The database still has to say no.
        await using var second = _store.LightweightSession();
        second.Insert(NewOperation("customer-b:key-2", holdId));
        var error = await Record.ExceptionAsync(() => second.SaveChangesAsync());

        Assert.NotNull(error);
        Assert.True(new MartenCheckoutOperationStore(_store.LightweightSession()).IsUniqueViolation(error));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Insert_must_refuse_a_duplicate_key_instead_of_upserting_over_the_winner()
    {
        var key = $"customer-c:{Guid.NewGuid():N}";
        await using (var session = _store.LightweightSession())
        {
            session.Insert(NewOperation(key, Guid.NewGuid()));
            await session.SaveChangesAsync();
        }

        var loser = NewOperation(key, Guid.NewGuid());
        await using var second = _store.LightweightSession();
        second.Insert(loser);
        var error = await Record.ExceptionAsync(() => second.SaveChangesAsync());

        Assert.NotNull(error);
        // Store() would have silently repointed the key at the loser's order. That is the bug this
        // whole record exists to prevent, so the distinction has to hold against real Postgres.
        await using var query = _store.QuerySession();
        var surviving = await query.LoadAsync<CheckoutOperation>(key);
        Assert.NotNull(surviving);
        Assert.NotEqual(loser.OrderId, surviving.OrderId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Order_stream_receipt_and_outbox_row_must_commit_or_fail_together()
    {
        var holdId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        await using (var winner = _store.LightweightSession())
        {
            await AcceptAsync(winner, customerId, holdId, "key-1");
        }

        // Second checkout for the same hold: the receipt insert fails, so the order stream and the
        // outbox row it was committed alongside must not exist either.
        var orphanOrderId = Guid.NewGuid();
        await using (var loser = _store.LightweightSession())
        {
            var error = await Record.ExceptionAsync(() =>
                AcceptAsync(loser, customerId, holdId, "key-2", orphanOrderId));
            Assert.NotNull(error);
        }

        await using var query = _store.QuerySession();
        Assert.Null(await query.Events.AggregateStreamAsync<Order>(orphanOrderId));
        Assert.Null(await query.LoadAsync<OrderDispatch>(orphanOrderId));
        Assert.Equal(1, await query.Query<CheckoutOperation>().CountAsync(x => x.HoldId == holdId));
        Assert.Equal(1, await query.Query<OrderDispatch>().CountAsync());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_checkouts_of_one_hold_must_leave_exactly_one_order_in_Postgres()
    {
        var holdId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        // Distinct idempotency keys, so only the hold's unique index can separate them.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            await using var session = _store.LightweightSession();
            try
            {
                await AcceptAsync(session, customerId, holdId, $"key-{index}");
                return true;
            }
            catch
            {
                return false;
            }
        }));

        Assert.Equal(1, attempts.Count(succeeded => succeeded));

        await using var query = _store.QuerySession();
        Assert.Equal(1, await query.Query<CheckoutOperation>().CountAsync(x => x.HoldId == holdId));
        Assert.Equal(1, await query.Query<OrderDispatch>().CountAsync());
    }

    private static async Task AcceptAsync(
        IDocumentSession session,
        Guid customerId,
        Guid holdId,
        string idempotencyKey,
        Guid? orderId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var seatIds = new[] { Guid.NewGuid() };
        var eventId = Guid.NewGuid();
        var placed = new OrderPlaced(
            orderId ?? Guid.NewGuid(), customerId, eventId, seatIds, 500_000m, "VND", now);

        session.Events.StartStream<Order>(placed.OrderId, placed);
        session.Insert(new CheckoutOperation
        {
            Id = CheckoutOperation.KeyFor(customerId, idempotencyKey),
            CustomerId = customerId,
            HoldId = holdId,
            OrderId = placed.OrderId,
            Fingerprint = "fingerprint",
            CreatedAt = now,
        });
        session.Insert(OrderDispatch.Pending(
            new OrderSubmitted(
                placed.OrderId, eventId, customerId, holdId, seatIds, 500_000m, "CreditCard",
                "Buyer", "buyer@example.com", now, "VND", 7, idempotencyKey, holdId.ToString(), 3),
            now));

        await session.SaveChangesAsync();
    }

    private static CheckoutOperation NewOperation(string id, Guid holdId) => new()
    {
        Id = id,
        CustomerId = Guid.NewGuid(),
        HoldId = holdId,
        OrderId = Guid.NewGuid(),
        Fingerprint = "fingerprint",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
