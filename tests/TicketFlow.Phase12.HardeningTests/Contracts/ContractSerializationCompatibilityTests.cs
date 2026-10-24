using System.Text.Json;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Contracts.Messaging.Checkout;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Reservation;

namespace TicketFlow.Phase12.HardeningTests.Contracts;

public sealed class ContractSerializationCompatibilityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", HardeningTraits.Harness)]
    public void Legacy_order_submitted_payload_uses_safe_v2_defaults()
    {
        const string legacyPayload = """
            {
              "orderId":"4c0da741-d2fb-4ec5-bb3d-539115924cad",
              "eventId":"7c8e38a7-cc3c-4d58-a2c3-17306c446504",
              "userId":"9aaf6a06-317e-42da-9bf5-f699ed148c0f",
              "holdId":"3b8ca87d-6fd2-4e0d-9362-f70d85bc24b0",
              "seatIds":["e0a52061-a60e-44c8-9c87-d64b598d596a"],
              "amount":125000,
              "paymentMethod":"card",
              "customerName":"Legacy customer",
              "customerEmail":"legacy@example.test",
              "submittedAt":"2026-07-20T00:00:00+00:00"
            }
            """;

        var message = JsonSerializer.Deserialize<OrderSubmitted>(legacyPayload, Json);

        Assert.NotNull(message);
        Assert.Equal("VND", message.Currency);
        Assert.Equal(0, message.PriceSnapshotVersion);
        Assert.Null(message.IdempotencyKey);
        Assert.Null(message.AllocationReference);
    }

    [Fact]
    [Trait("Category", HardeningTraits.Harness)]
    public void Legacy_catalog_payload_uses_revision_zero()
    {
        const string legacyPayload = """
            { "catalogEventId":"19bf019e-dbee-48c9-855d-248f0a3638e0" }
            """;

        var message = JsonSerializer.Deserialize<CatalogEventCancelled>(legacyPayload, Json);

        Assert.NotNull(message);
        Assert.Equal(0, message.AggregateVersion);
        Assert.Null(message.OccurredAt);
    }

    [Fact]
    [Trait("Category", HardeningTraits.Harness)]
    public void V2_checkout_and_allocation_contracts_round_trip_without_authority_fields()
    {
        var checkout = new CheckoutIntentV2(Guid.NewGuid(), "card", "checkout-7", "Ada", "ada@example.test");
        var quote = new HoldQuoteV2(
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], 125000m, "VND", 42,
            DateTimeOffset.Parse("2026-07-20T01:00:00+00:00"));

        var restoredCheckout = JsonSerializer.Deserialize<CheckoutIntentV2>(JsonSerializer.Serialize(checkout, Json), Json);
        var restoredQuote = JsonSerializer.Deserialize<HoldQuoteV2>(JsonSerializer.Serialize(quote, Json), Json);

        Assert.Equal(checkout, restoredCheckout);
        Assert.NotNull(restoredQuote);
        Assert.Equal(quote.HoldId, restoredQuote.HoldId);
        Assert.Equal(quote.EventId, restoredQuote.EventId);
        Assert.Equal(quote.SeatIds, restoredQuote.SeatIds);
        Assert.Equal(quote.Amount, restoredQuote.Amount);
        Assert.Equal(quote.Currency, restoredQuote.Currency);
        Assert.Equal(quote.PriceSnapshotVersion, restoredQuote.PriceSnapshotVersion);
        Assert.Equal(quote.ExpiresAt, restoredQuote.ExpiresAt);
    }
}
