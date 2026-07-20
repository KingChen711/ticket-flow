using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Phase12.HardeningTests.Checkout;

/// <summary>
/// Desired server-authoritative checkout invariants. These tests are intentionally red in
/// Commit 1: PlaceOrderCommand still carries client-owned seats, amount, currency and event id,
/// and the handler has no hold-quote/sale-policy dependency with which to validate them.
/// </summary>
public sealed class CheckoutAuthorityTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Hold_for_seat_A_must_not_create_an_order_or_ticket_intent_for_client_seat_B()
    {
        var heldSeatA = Guid.NewGuid();
        var tamperedSeatB = Guid.NewGuid();
        var (handler, store, publisher) = CreateHandler();

        // Invariant: the server must derive [heldSeatA] from HoldId. The current handler never
        // loads that quote and accepts [tamperedSeatB] directly from the request.
        var error = await Record.ExceptionAsync(() => handler.HandleAsync(
            Command(seatIds: [tamperedSeatB], holdId: Guid.NewGuid())));

        Assert.NotNull(error);
        Assert.Equal(0, store.StreamCount);
        Assert.Empty(publisher.Published);
        Assert.NotEqual(heldSeatA, tamperedSeatB); // Documents that this is not a duplicate-id case.
    }

    public static TheoryData<decimal, string> InvalidMoney => new()
    {
        { 0m, "VND" },
        { 500_000m, "USD" },
        { 500_000m, "NOT-A-CURRENCY" },
    };

    [Theory]
    [MemberData(nameof(InvalidMoney))]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Client_amount_or_currency_tampering_must_fail_before_any_side_effect(
        decimal clientAmount,
        string clientCurrency)
    {
        var (handler, store, publisher) = CreateHandler();

        var error = await Record.ExceptionAsync(() => handler.HandleAsync(
            Command(totalAmount: clientAmount, currency: clientCurrency)));

        Assert.NotNull(error);
        Assert.Equal(0, store.StreamCount);
        Assert.Empty(publisher.Published);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Cancelled")]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Draft_or_cancelled_event_must_be_rejected_before_order_persistence(string nonSaleableStatus)
    {
        _ = nonSaleableStatus; // Status exists in Catalog, but the current Ordering contract cannot carry/verify it.
        var (handler, store, publisher) = CreateHandler();

        var error = await Record.ExceptionAsync(() => handler.HandleAsync(Command()));

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

    private static (PlaceOrderCommandHandler Handler, RecordingOrderStore Store, RecordingIntegrationEventPublisher Publisher)
        CreateHandler()
    {
        var store = new RecordingOrderStore();
        var publisher = new RecordingIntegrationEventPublisher();
        return (new PlaceOrderCommandHandler(store, publisher, TimeProvider.System), store, publisher);
    }

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
