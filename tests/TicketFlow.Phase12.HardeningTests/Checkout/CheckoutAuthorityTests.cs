using System.Text.Json;
using TicketFlow.Contracts.Messaging.Checkout;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Abstractions;
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
        var (handler, store, publisher) = CreateV2Handler(quote);

        await handler.HandleAsync(V2Command(quote));

        var submitted = Assert.IsType<OrderSubmitted>(Assert.Single(publisher.Published));
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
        var (handler, store, publisher) = CreateV2Handler(quote);
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

        var submitted = Assert.IsType<OrderSubmitted>(Assert.Single(publisher.Published));
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
        var publisher = new RecordingIntegrationEventPublisher();
        var handler = new PlaceOrderV2CommandHandler(
            store, publisher, new RecordingHoldQuoteProvider(null), TimeProvider.System);
        var quote = Quote();

        var error = await Record.ExceptionAsync(() => handler.HandleAsync(V2Command(quote)));

        Assert.NotNull(error);
        Assert.Equal(0, store.StreamCount);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Crash_after_order_commit_before_publish_then_retry_must_reuse_one_order_stream()
    {
        var store = new RecordingOrderStore();
        var publisher = new RecordingIntegrationEventPublisher { FailNextPublish = true };
        var handler = new PlaceOrderCommandHandler(store, publisher, TimeProvider.System);
        var command = Command();

        // Failpoint: Marten SaveChanges completed, broker publish did not.
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(command));

        await handler.HandleAsync(command);

        // Desired durable operation receipt/outbox behavior. Current retry creates a new OrderId.
        Assert.Equal(1, store.StreamCount);
        Assert.Single(publisher.Published);
    }

    private static (PlaceOrderV2CommandHandler Handler, RecordingOrderStore Store, RecordingIntegrationEventPublisher Publisher)
        CreateV2Handler(AuthoritativeHoldQuote quote)
    {
        var store = new RecordingOrderStore();
        var publisher = new RecordingIntegrationEventPublisher();
        return (
            new PlaceOrderV2CommandHandler(store, publisher, new RecordingHoldQuoteProvider(quote), TimeProvider.System),
            store,
            publisher);
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

internal sealed class RecordingOrderStore : IOrderStore
{
    private readonly Dictionary<Guid, List<object>> _streams = [];

    public int StreamCount => _streams.Count;

    public Task<Order?> LoadAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_streams.TryGetValue(orderId, out var events) ? Order.Load(events) : null);

    public void StartStream(Guid orderId, object initialEvent) => _streams[orderId] = [initialEvent];

    public void Append(Guid orderId, object @event) => _streams[orderId].Add(@event);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
