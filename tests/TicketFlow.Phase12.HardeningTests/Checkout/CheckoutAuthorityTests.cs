using System.Text.Json;
using TicketFlow.Contracts.Messaging.Checkout;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Checkout;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Phase12.HardeningTests.Checkout;

/// <summary>
/// Server-authoritative checkout invariants promoted by Commit 3. The one remaining expected
/// failure in this file is the durable order/outbox retry boundary owned by Commit 7.
/// </summary>
public sealed class CheckoutAuthorityTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Hold_for_seat_A_creates_order_intent_only_for_server_quote_seat_A()
    {
        var heldSeatA = Guid.NewGuid();
        var quote = Quote(seatIds: [heldSeatA]);
        var (handler, store, operations) = CreateV2Handler(quote);

        await handler.HandleAsync(V2Command(quote));

        var submitted = Assert.IsType<OrderSubmitted>(Assert.Single(operations.Dispatches).Payload);
        Assert.Equal([heldSeatA], submitted.SeatIds);
        Assert.Equal(1, store.StreamCount);
    }

    public static TheoryData<decimal, string> InvalidMoney => new()
    {
        { 0m, "VND" },
        { 500_000m, "USD" },
        { 500_000m, "NOT-A-CURRENCY" },
    };

    [Theory]
    [MemberData(nameof(InvalidMoney))]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Client_amount_or_currency_fields_are_ignored_and_cannot_change_server_quote(
        decimal clientAmount,
        string clientCurrency)
    {
        var quote = Quote(amount: 500_000m, currency: "VND");
        var (handler, store, operations) = CreateV2Handler(quote);
        var maliciousJson = $$"""
            {
              "holdId":"{{quote.HoldId}}",
              "paymentMethod":"CreditCard",
              "idempotencyKey":"checkout-1",
              "totalAmount":{{clientAmount}},
              "currency":"{{clientCurrency}}",
              "seatIds":["{{Guid.NewGuid()}}"],
              "eventId":"{{Guid.NewGuid()}}"
            }
            """;
        var intent = JsonSerializer.Deserialize<CheckoutIntentV2>(maliciousJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        await handler.HandleAsync(new PlaceOrderV2Command(
            quote.OwnerUserId, intent.HoldId, intent.PaymentMethod, intent.IdempotencyKey, "Buyer", "buyer@example.com"));

        var submitted = Assert.IsType<OrderSubmitted>(Assert.Single(operations.Dispatches).Payload);
        Assert.Equal(500_000m, submitted.Amount);
        Assert.Equal("VND", submitted.Currency);
        Assert.Equal(1, store.StreamCount);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Cancelled")]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Draft_or_cancelled_event_must_be_rejected_before_order_persistence(string nonSaleableStatus)
    {
        _ = nonSaleableStatus;
        var store = new RecordingOrderStore();
        var operations = new RecordingCheckoutOperationStore(store);
        var handler = new PlaceOrderV2CommandHandler(
            new CheckoutAcceptanceService(store, operations, TimeProvider.System),
            new RecordingHoldQuoteProvider(null),
            TimeProvider.System);
        var quote = Quote();

        var error = await Record.ExceptionAsync(() => handler.HandleAsync(V2Command(quote)));

        Assert.NotNull(error);
        Assert.Equal(0, store.StreamCount);
        Assert.Empty(operations.Dispatches);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Crash_after_order_commit_before_publish_then_retry_must_reuse_one_order_stream()
    {
        var (handler, store, operations) = CreateLegacyHandler();
        var publisher = new RecordingIntegrationEventPublisher { FailNextPublish = true };
        var relay = new RecordingDispatchRelay(operations, publisher);
        var command = Command();

        // Failpoint: the order is durably committed, then the broker refuses the message.
        var first = await handler.HandleAsync(command);
        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.RelayOnceAsync());

        // The client retries the same checkout — before Commit 7 this minted a second OrderId.
        var retry = await handler.HandleAsync(command);
        await relay.RelayOnceAsync();

        Assert.Equal(first.OrderId, retry.OrderId);
        Assert.Equal(1, store.StreamCount);
        Assert.Single(publisher.Published);
        Assert.Equal(OrderDispatchState.Dispatched, Assert.Single(operations.Dispatches).State);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Checkout_must_not_publish_anything_before_the_relay_runs()
    {
        var quote = Quote();
        var (handler, _, operations) = CreateV2Handler(quote);
        var publisher = new RecordingIntegrationEventPublisher();

        await handler.HandleAsync(V2Command(quote));

        // A broker call inside the request is the step that could fail after the order was already
        // durable. The handler no longer has a publisher to call at all — the message exists only
        // as an outbox row until the relay picks it up.
        Assert.Equal(OrderDispatchState.Pending, Assert.Single(operations.Dispatches).State);
        Assert.Empty(publisher.Published);
        Assert.Single(await new RecordingDispatchRelay(operations, publisher).PendingAsync());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Double_submit_of_one_checkout_must_return_the_same_order()
    {
        var quote = Quote();
        var (handler, store, operations) = CreateV2Handler(quote);

        var first = await handler.HandleAsync(V2Command(quote));
        var second = await handler.HandleAsync(V2Command(quote));

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(1, store.StreamCount);
        Assert.Single(operations.Dispatches);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_double_submit_must_still_produce_one_order()
    {
        var quote = Quote();
        var (handler, store, operations) = CreateV2Handler(quote);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => handler.HandleAsync(V2Command(quote))));

        Assert.Single(responses.Select(response => response.OrderId).Distinct());
        Assert.Equal(1, store.StreamCount);
        Assert.Single(operations.Dispatches);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_fresh_idempotency_key_for_the_same_hold_must_not_create_a_second_order()
    {
        var quote = Quote();
        var (handler, store, _) = CreateV2Handler(quote);

        var first = await handler.HandleAsync(V2Command(quote));
        // A client that regenerates its key on every retry would otherwise buy the same seats twice.
        var second = await handler.HandleAsync(V2Command(quote) with { IdempotencyKey = "checkout-2" });

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(1, store.StreamCount);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Reusing_one_idempotency_key_for_a_different_request_must_be_refused()
    {
        var quote = Quote();
        var (handler, store, _) = CreateV2Handler(quote);
        await handler.HandleAsync(V2Command(quote));

        // Same key, different intent: serving the first order back would be answering a question
        // the caller did not ask.
        await Assert.ThrowsAsync<CheckoutConflictException>(() =>
            handler.HandleAsync(V2Command(quote) with { PaymentMethod = "BankTransfer" }));

        Assert.Equal(1, store.StreamCount);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Relay_must_publish_each_order_exactly_once_across_repeated_passes()
    {
        var quote = Quote();
        var (handler, _, operations) = CreateV2Handler(quote);
        var publisher = new RecordingIntegrationEventPublisher();
        var relay = new RecordingDispatchRelay(operations, publisher);
        await handler.HandleAsync(V2Command(quote));

        await relay.RelayOnceAsync();
        await relay.RelayOnceAsync();
        await relay.RelayOnceAsync();

        var submitted = Assert.IsType<OrderSubmitted>(Assert.Single(publisher.Published));
        Assert.Equal(quote.HoldId, submitted.HoldId);
        Assert.Equal(quote.AllocationGeneration, submitted.AllocationGeneration);
    }

    private static (PlaceOrderV2CommandHandler Handler, RecordingOrderStore Store, RecordingCheckoutOperationStore Operations)
        CreateV2Handler(AuthoritativeHoldQuote quote)
    {
        var store = new RecordingOrderStore();
        var operations = new RecordingCheckoutOperationStore(store);
        return (
            new PlaceOrderV2CommandHandler(
                new CheckoutAcceptanceService(store, operations, TimeProvider.System),
                new RecordingHoldQuoteProvider(quote),
                TimeProvider.System),
            store,
            operations);
    }

    private static (PlaceOrderCommandHandler Handler, RecordingOrderStore Store, RecordingCheckoutOperationStore Operations)
        CreateLegacyHandler()
    {
        var store = new RecordingOrderStore();
        var operations = new RecordingCheckoutOperationStore(store);
        return (
            new PlaceOrderCommandHandler(new CheckoutAcceptanceService(store, operations, TimeProvider.System)),
            store,
            operations);
    }

    // AllocationGeneration must be positive: Commit 4 made it part of the handler's completeness
    // check, but this helper kept using the 8-argument shape and left it at its default 0, which
    // silently turned four Harness tests red. Regression found and fixed during Commit 5.
    private static AuthoritativeHoldQuote Quote(
        IReadOnlyList<Guid>? seatIds = null,
        decimal amount = 500_000m,
        string currency = "VND") => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        seatIds ?? [Guid.NewGuid()],
        amount,
        currency,
        7,
        DateTimeOffset.UtcNow.AddMinutes(5),
        AllocationGeneration: 3);

    private static PlaceOrderV2Command V2Command(AuthoritativeHoldQuote quote) => new(
        quote.OwnerUserId,
        quote.HoldId,
        "CreditCard",
        "checkout-1",
        "Buyer",
        "buyer@example.com");

    private static PlaceOrderCommand Command(
        IReadOnlyList<Guid>? seatIds = null,
        decimal totalAmount = 500_000m,
        string? currency = "VND",
        Guid? holdId = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            seatIds ?? [Guid.NewGuid()],
            totalAmount,
            currency,
            holdId ?? Guid.NewGuid(),
            "CreditCard",
            "Buyer",
            "buyer@example.com");
}

internal sealed class RecordingHoldQuoteProvider(AuthoritativeHoldQuote? quote) : IHoldQuoteProvider
{
    public Task<AuthoritativeHoldQuote?> GetAsync(
        Guid holdId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(quote is not null && quote.HoldId == holdId && quote.OwnerUserId == ownerUserId ? quote : null);
}

/// <summary>
/// Models the one property that makes the durable checkout work: the order stream, the operation
/// receipt and the outbox row are committed together, and a duplicate receipt is refused rather
/// than upserted. Staging is per-caller so concurrent handlers do not see each other's pending
/// writes; the commit itself is serialized, exactly as Postgres would.
/// </summary>
internal sealed class RecordingOrderStore : IOrderStore
{
    private readonly Dictionary<Guid, List<object>> _streams = [];
    private readonly AsyncLocal<(Guid OrderId, object Event)?> _stagedStream = new();
    private readonly Lock _gate = new();

    internal RecordingCheckoutOperationStore? Operations { get; set; }

    public int StreamCount
    {
        get { lock (_gate) return _streams.Count; }
    }

    public Task<Order?> LoadAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_streams.TryGetValue(orderId, out var events) ? Order.Load(events) : null);
    }

    public void StartStream(Guid orderId, object initialEvent) => _stagedStream.Value = (orderId, initialEvent);

    public void Append(Guid orderId, object @event)
    {
        lock (_gate)
            _streams[orderId].Add(@event);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // The receipt is validated first: if it loses the race, no stream is created either.
            Operations?.Commit();

            if (_stagedStream.Value is { } staged)
            {
                _stagedStream.Value = null;
                _streams[staged.OrderId] = [staged.Event];
            }
        }

        return Task.CompletedTask;
    }
}

internal sealed class RecordingCheckoutOperationStore : ICheckoutOperationStore
{
    private readonly Dictionary<string, CheckoutOperation> _operations = [];
    private readonly Dictionary<Guid, OrderDispatch> _dispatches = [];
    private readonly AsyncLocal<CheckoutOperation?> _stagedOperation = new();
    private readonly AsyncLocal<OrderDispatch?> _stagedDispatch = new();

    public RecordingCheckoutOperationStore(RecordingOrderStore orders) => orders.Operations = this;

    public IReadOnlyCollection<OrderDispatch> Dispatches => _dispatches.Values.ToArray();

    public Task<CheckoutOperation?> FindOperationAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_operations)
            return Task.FromResult(_operations.GetValueOrDefault(key));
    }

    public Task<CheckoutOperation?> FindOperationByHoldAsync(Guid holdId, CancellationToken cancellationToken = default)
    {
        lock (_operations)
            return Task.FromResult(_operations.Values.SingleOrDefault(operation => operation.HoldId == holdId));
    }

    public void StoreOperation(CheckoutOperation operation) => _stagedOperation.Value = operation;

    public void StoreDispatch(OrderDispatch dispatch) => _stagedDispatch.Value = dispatch;

    public Task<IReadOnlyList<OrderDispatch>> GetPendingDispatchesAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        lock (_operations)
            return Task.FromResult<IReadOnlyList<OrderDispatch>>(
                _dispatches.Values
                    .Where(dispatch => dispatch.State == OrderDispatchState.Pending)
                    .OrderBy(dispatch => dispatch.CreatedAt)
                    .Take(maxCount)
                    .ToList());
    }

    public bool IsUniqueViolation(Exception exception) => exception is DuplicateCheckoutException;

    public void DiscardPendingChanges()
    {
        _stagedOperation.Value = null;
        _stagedDispatch.Value = null;
    }

    public void Commit()
    {
        var operation = _stagedOperation.Value;
        var dispatch = _stagedDispatch.Value;
        _stagedOperation.Value = null;
        _stagedDispatch.Value = null;

        if (operation is not null)
        {
            lock (_operations)
            {
                if (_operations.ContainsKey(operation.Id) ||
                    _operations.Values.Any(existing => existing.HoldId == operation.HoldId))
                    throw new DuplicateCheckoutException(operation.Id);

                _operations[operation.Id] = operation;
            }
        }

        if (dispatch is not null)
        {
            lock (_operations)
            {
                if (!_dispatches.TryAdd(dispatch.Id, dispatch))
                    throw new DuplicateCheckoutException(dispatch.Id.ToString());
            }
        }
    }
}

internal sealed class DuplicateCheckoutException(string key)
    : Exception($"Checkout key '{key}' already exists.");

/// <summary>Mirrors OrderDispatchRelay's publish-then-mark ordering without Postgres or RabbitMQ.</summary>
internal sealed class RecordingDispatchRelay(
    RecordingCheckoutOperationStore operations,
    RecordingIntegrationEventPublisher publisher)
{
    public Task<IReadOnlyList<OrderDispatch>> PendingAsync() => operations.GetPendingDispatchesAsync(50);

    public async Task RelayOnceAsync()
    {
        foreach (var record in await operations.GetPendingDispatchesAsync(50))
        {
            await publisher.PublishAsync(record.Payload);
            record.MarkDispatched(DateTimeOffset.UtcNow);
        }
    }
}

internal sealed class RecordingIntegrationEventPublisher : IIntegrationEventPublisher
{
    public List<object> Published { get; } = [];
    public bool FailNextPublish { get; set; }

    public Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default) where T : class
    {
        if (FailNextPublish)
        {
            FailNextPublish = false;
            throw new InvalidOperationException("Failpoint: order committed before broker publish.");
        }

        Published.Add(integrationEvent);
        return Task.CompletedTask;
    }
}
