using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Catalog.Domain.Events;
using TicketFlow.Catalog.Infrastructure.EventDetails;
using TicketFlow.Ordering.Domain.Orders;
using TicketFlow.Ordering.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Web;

public sealed class WebContractTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Asia_Saigon_local_time_keeps_IANA_zone_and_is_stored_as_UTC()
    {
        var schedule = EventSchedule.Create(
            DateTimeOffset.Parse("2026-07-26T12:30:00Z"),
            DateTimeOffset.Parse("2026-07-26T15:00:00Z"),
            "Asia/Saigon",
            startUtcOffsetMinutes: 420,
            endUtcOffsetMinutes: 420);

        Assert.Equal(TimeSpan.Zero, schedule.StartsAt.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-07-26T12:30:00Z"), schedule.StartsAt);
        Assert.Equal("Asia/Saigon", schedule.TimeZoneId);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Browser_offset_that_disagrees_with_IANA_zone_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => EventSchedule.Create(
            DateTimeOffset.Parse("2026-07-26T12:30:00Z"),
            DateTimeOffset.Parse("2026-07-26T15:00:00Z"),
            "Asia/Saigon",
            startUtcOffsetMinutes: 0,
            endUtcOffsetMinutes: 0));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Order_projection_advances_sequence_for_every_stream_event()
    {
        var orderId = Guid.NewGuid();
        var projection = new MyOrdersProjection();
        var view = MyOrdersProjection.Create(new OrderPlaced(
            orderId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [Guid.NewGuid()],
            500_000m,
            "VND",
            DateTimeOffset.UtcNow));

        view = projection.Apply(new OrderSeatsConfirmed(orderId, DateTimeOffset.UtcNow), view);
        view = projection.Apply(new OrderConfirmed(orderId, DateTimeOffset.UtcNow), view);

        Assert.Equal(3, view.Sequence);
        Assert.Equal(nameof(OrderStatus.Confirmed), view.Status);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Empty_availability_is_distinct_from_reservation_outage()
    {
        var emptyClient = new HttpSeatAvailabilityClient(
            Client(HttpStatusCode.OK, """{"eventId":"00000000-0000-0000-0000-000000000001","seats":[]}"""),
            NullLogger<HttpSeatAvailabilityClient>.Instance);
        var outageClient = new HttpSeatAvailabilityClient(
            Client(HttpStatusCode.ServiceUnavailable, "{}"),
            NullLogger<HttpSeatAvailabilityClient>.Instance);

        var empty = await emptyClient.GetAvailabilityAsync(Guid.NewGuid());
        var outage = await outageClient.GetAvailabilityAsync(Guid.NewGuid());

        Assert.True(empty.IsAvailable);
        Assert.Empty(empty.Seats);
        Assert.False(outage.IsAvailable);
        Assert.Empty(outage.Seats);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            emptyClient.GetAvailabilityAsync(Guid.NewGuid(), cancellation.Token));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Public_read_only_hub_does_not_require_a_browser_bearer_token()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "src", "Gateway", "TicketFlow.Gateway", "appsettings.json")));
        var route = document.RootElement
            .GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .GetProperty("reservation-hub-route");

        Assert.Equal("/reservation/hubs/{**catch-all}", route.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal("anonymous", route.GetProperty("AuthorizationPolicy").GetString());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Public_event_recommendations_do_not_require_a_browser_bearer_token()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "src", "Gateway", "TicketFlow.Gateway", "appsettings.json")));
        var route = document.RootElement
            .GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .GetProperty("recommendation-route");

        Assert.Equal("/recommendation/{**catch-all}", route.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal("anonymous", route.GetProperty("AuthorizationPolicy").GetString());
    }

    private static HttpClient Client(HttpStatusCode statusCode, string body) => new(new StubHandler(statusCode, body))
    {
        BaseAddress = new Uri("http://reservation.test"),
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TicketFlow.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("TicketFlow repository root was not found.");
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
    }
}
