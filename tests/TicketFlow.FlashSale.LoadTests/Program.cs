using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NBomber.CSharp;

// Phase 11's full-stack flash-sale evidence.  Unlike Reservation.LoadTests, this deliberately
// crosses the public gateway and lets PurchaseSaga drive confirmation, payment and ticketing.
// It is an executable load test, not a unit test; run it only while the Compose stack is up.
var settings = FlashSaleSettings.Load();
Directory.CreateDirectory(settings.ReportDirectory);

using var handler = new SocketsHttpHandler
{
    // NBomber still activates all M virtual users at once.  The Compose gateway is exposed
    // through Docker Desktop's Windows host-port NAT, which cannot reliably open 300 new TCP
    // sockets in one instant on this machine; cap the physical pool and let HttpClient queue the
    // excess users.  That keeps the intended 300-user contention test while avoiding host-NAT
    // connection drops that never reach the application.  Override for a larger environment.
    MaxConnectionsPerServer = settings.MaxConnectionsPerServer
};
using var gateway = new HttpClient(handler)
{
    BaseAddress = new Uri(settings.GatewayBaseUrl),
    Timeout = TimeSpan.FromSeconds(45)
};
using var keycloak = new HttpClient { BaseAddress = new Uri(settings.KeycloakBaseUrl), Timeout = TimeSpan.FromSeconds(30) };

Console.WriteLine("TicketFlow flash-sale load test (Compose stack only)");
Console.WriteLine($"Gateway: {settings.GatewayBaseUrl}");
Console.WriteLine($"Shape: {settings.VirtualUsers} virtual users competing for {settings.SeatCount} seats.");

// Tokens are intentionally acquired once, before NBomber starts.  The test measures seat
// contention and PurchaseSaga throughput, rather than Keycloak's password-grant throughput.
// Passwords are required from the environment and are never logged or stored in source.
var organizerToken = await GetAccessTokenAsync(keycloak, settings.OrganizerUsername, settings.OrganizerPassword);
var customerToken = await GetAccessTokenAsync(keycloak, settings.CustomerUsername, settings.CustomerPassword);

var flashSale = await CreateFlashSaleAsync(gateway, organizerToken, settings);
Console.WriteLine($"Provisioned event {flashSale.EventId} with {flashSale.SeatIds.Count} seats.");

var acceptedOrderIds = new ConcurrentBag<Guid>();
var unexpectedFailures = new ConcurrentQueue<string>();
var attempts = 0;
var soldOut = 0;
var held = 0;
var ordersCreated = 0;
var unexpected = 0;
var seatCursor = -1;
var startedAt = DateTimeOffset.UtcNow;
var stopwatch = Stopwatch.StartNew();

var scenario = Scenario.Create("flash_sale_hold_then_order", async _ =>
    {
        Interlocked.Increment(ref attempts);
        var seatId = flashSale.SeatIds[(int)((uint)Interlocked.Increment(ref seatCursor) % flashSale.SeatIds.Count)];

        try
        {
            using var holdResponse = await SendJsonAsync(
                gateway,
                HttpMethod.Post,
                "/reservation/holds",
                new { eventId = flashSale.EventId, seatIds = new[] { seatId } },
                customerToken,
                CancellationToken.None);

            // A sold-out response is the expected result for users who lose the race.  It is
            // successful test behaviour, not an NBomber error, and therefore remains visible as
            // its own status code in the generated report.
            if (holdResponse.StatusCode == HttpStatusCode.Conflict)
            {
                Interlocked.Increment(ref soldOut);
                return Response.Ok(statusCode: "409_sold_out");
            }

            if (!holdResponse.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(holdResponse);
                Interlocked.Increment(ref unexpected);
                unexpectedFailures.Enqueue($"hold: HTTP {(int)holdResponse.StatusCode} {holdResponse.StatusCode}; {body}");
                return Response.Fail(statusCode: $"hold_{(int)holdResponse.StatusCode}");
            }

            var hold = await ReadJsonAsync<HoldResponse>(holdResponse);
            if (!hold.Success || hold.HoldId is not { } holdId)
            {
                Interlocked.Increment(ref unexpected);
                unexpectedFailures.Enqueue("hold returned a successful HTTP status without a usable hold id");
                return Response.Fail(statusCode: "hold_invalid_response");
            }

            Interlocked.Increment(ref held);

            using var orderResponse = await SendJsonAsync(
                gateway,
                HttpMethod.Post,
                "/ordering/orders",
                new
                {
                    catalogEventId = flashSale.EventId,
                    seatIds = new[] { seatId },
                    totalAmount = 100_000m,
                    currency = "VND",
                    holdId,
                    paymentMethod = "card"
                },
                customerToken,
                CancellationToken.None);

            if (orderResponse.StatusCode != HttpStatusCode.Created)
            {
                var body = await ReadBodyAsync(orderResponse);
                Interlocked.Increment(ref unexpected);
                unexpectedFailures.Enqueue($"order: HTTP {(int)orderResponse.StatusCode} {orderResponse.StatusCode}; {body}");
                return Response.Fail(statusCode: $"order_{(int)orderResponse.StatusCode}");
            }

            var order = await ReadJsonAsync<OrderResponse>(orderResponse);
            acceptedOrderIds.Add(order.OrderId);
            Interlocked.Increment(ref ordersCreated);
            return Response.Ok(statusCode: "201_order_created");
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref unexpected);
            unexpectedFailures.Enqueue($"{exception.GetType().Name}: {exception.Message}");
            return Response.Fail(statusCode: "exception");
        }
    })
    // Exactly M one-shot virtual users.  Disabling automatic restarts keeps the run's 300
    // attempts honest: an unexpected failure is reported, never retried into a larger sample.
    .WithLoadSimulations(Simulation.IterationsForConstant(copies: settings.VirtualUsers, iterations: settings.VirtualUsers))
    .WithRestartIterationOnFail(false);

NBomberRunner
    .RegisterScenarios(scenario)
    .WithReportFolder(settings.ReportDirectory)
    .WithReportFileName("flash-sale")
    .Run();

stopwatch.Stop();

var accepted = acceptedOrderIds.Distinct().OrderBy(id => id).ToArray();
var finalStatuses = await WaitForOrdersToSettleAsync(gateway, customerToken, accepted, settings.SettleTimeout);
var finalAvailability = await GetJsonAsync<EventAvailabilityResponse>(
    gateway,
    $"/reservation/events/{flashSale.EventId}/availability",
    customerToken,
    CancellationToken.None);

var statusCounts = finalStatuses.Values
    .GroupBy(status => status, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
var confirmed = CountStatus(statusCounts, "Confirmed");
var cancelled = CountStatus(statusCounts, "Cancelled");
var pendingOrMissing = accepted.Length - confirmed - cancelled;
var seatsSold = finalAvailability.Seats.Count(seat => string.Equals(seat.Status, "Sold", StringComparison.OrdinalIgnoreCase));

// The normal happy-path result is exactly N confirmed orders.  More than N would prove an
// oversell defect; fewer or non-terminal orders prove the full stack did not settle cleanly and
// also fail the evidence run rather than being silently called a pass.
var passed = attempts == settings.VirtualUsers
             && unexpected == 0
             && ordersCreated == settings.SeatCount
             && confirmed == settings.SeatCount
             && cancelled == 0
             && pendingOrMissing == 0
             && seatsSold == settings.SeatCount
             && seatsSold <= settings.SeatCount;

var summary = new FlashSaleSummary(
    DateTimeOffset.UtcNow,
    settings.GatewayBaseUrl,
    flashSale.EventId,
    settings.SeatCount,
    settings.VirtualUsers,
    attempts,
    held,
    soldOut,
    ordersCreated,
    confirmed,
    cancelled,
    pendingOrMissing,
    seatsSold,
    unexpected,
    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
    statusCounts,
    unexpectedFailures.Take(20).ToArray(),
    passed);

var summaryPath = Path.Combine(settings.ReportDirectory, "flash-sale-summary.json");
await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, JsonOptionsHolder.Value));

Console.WriteLine();
Console.WriteLine($"Attempts              : {attempts}/{settings.VirtualUsers}");
Console.WriteLine($"Orders created         : {ordersCreated}");
Console.WriteLine($"Confirmed / cancelled  : {confirmed} / {cancelled}");
Console.WriteLine($"Pending or unavailable : {pendingOrMissing}");
Console.WriteLine($"Seats sold             : {seatsSold}/{settings.SeatCount}");
Console.WriteLine($"Expected sold-out 409s : {soldOut}");
Console.WriteLine($"Unexpected failures    : {unexpected}");
Console.WriteLine($"NBomber report + summary: {settings.ReportDirectory}");
Console.WriteLine(passed
    ? "PASS — exactly the provisioned seats were confirmed; no oversell."
    : "FAIL — inspect the NBomber report and flash-sale-summary.json before accepting this run.");

Environment.ExitCode = passed ? 0 : 1;

static async Task<FlashSaleFixture> CreateFlashSaleAsync(HttpClient gateway, string organizerToken, FlashSaleSettings settings)
{
    var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
    var venue = await PostAndReadAsync<VenueResponse>(
        gateway,
        "/catalog/venues/",
        new { name = $"Flash Sale Venue {suffix}", street = "11 Load Test Street", city = "Ho Chi Minh City", country = "VN", postalCode = "700000" },
        organizerToken);

    var startsAt = DateTimeOffset.UtcNow.AddDays(30);
    var @event = await PostAndReadAsync<EventResponse>(
        gateway,
        "/catalog/events/",
        new
        {
            title = $"Flash Sale {suffix}",
            description = "Ephemeral full-stack load-test event.",
            artist = "TicketFlow Load Test",
            venueId = venue.Id,
            startsAt,
            endsAt = startsAt.AddHours(2)
        },
        organizerToken);

    _ = await PostAndReadAsync<SeatMapResponse>(gateway, $"/catalog/events/{@event.Id}/seatmap/", new { }, organizerToken);
    var seatMap = await PostAndReadAsync<SeatMapResponse>(
        gateway,
        $"/catalog/events/{@event.Id}/seatmap/sections",
        new { name = "Flash-sale GA", capacity = settings.SeatCount },
        organizerToken);
    var section = seatMap.Sections.Single();

    _ = await PostAndReadAsync<EventResponse>(
        gateway,
        $"/catalog/events/{@event.Id}/price-tiers",
        new { sectionId = section.Id, amount = 100_000m, currency = "VND" },
        organizerToken);
    _ = await PostAndReadAsync<EventResponse>(gateway, $"/catalog/events/{@event.Id}/publish", new { }, organizerToken);

    using var provision = await SendJsonAsync(
        gateway,
        HttpMethod.Post,
        $"/reservation/events/{@event.Id}/inventory",
        new { sections = new[] { new { sectionId = section.Id, sectionName = section.Name, capacity = section.Capacity } } },
        organizerToken,
        CancellationToken.None);
    await EnsureSuccessAsync(provision, "provision reservation inventory");

    var availability = await GetJsonAsync<EventAvailabilityResponse>(
        gateway,
        $"/reservation/events/{@event.Id}/availability",
        organizerToken,
        CancellationToken.None);
    if (availability.Seats.Count != settings.SeatCount)
        throw new InvalidOperationException($"Inventory created {availability.Seats.Count} seats, expected {settings.SeatCount}.");

    return new FlashSaleFixture(@event.Id, availability.Seats.Select(seat => seat.Id).ToArray());
}

static async Task<string> GetAccessTokenAsync(HttpClient keycloak, string username, string password)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/realms/ticketflow/protocol/openid-connect/token")
    {
        Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "ticketflow-dev",
            ["username"] = username,
            ["password"] = password
        })
    };
    using var response = await keycloak.SendAsync(request);
    await EnsureSuccessAsync(response, $"obtain access token for '{username}'");
    var token = await ReadJsonAsync<TokenResponse>(response);
    return string.IsNullOrWhiteSpace(token.AccessToken)
        ? throw new InvalidOperationException($"Keycloak returned no access token for '{username}'.")
        : token.AccessToken;
}

static async Task<T> PostAndReadAsync<T>(HttpClient client, string path, object body, string token)
{
    using var response = await SendJsonAsync(client, HttpMethod.Post, path, body, token, CancellationToken.None);
    await EnsureSuccessAsync(response, $"POST {path}");
    return await ReadJsonAsync<T>(response);
}

static async Task<HttpResponseMessage> SendJsonAsync(
    HttpClient client,
    HttpMethod method,
    string path,
    object body,
    string token,
    CancellationToken cancellationToken)
{
    using var request = new HttpRequestMessage(method, path)
    {
        Content = JsonContent.Create(body, options: JsonOptionsHolder.Value)
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return await client.SendAsync(request, cancellationToken);
}

static async Task<T> GetJsonAsync<T>(HttpClient client, string path, string token, CancellationToken cancellationToken)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await client.SendAsync(request, cancellationToken);
    await EnsureSuccessAsync(response, $"GET {path}");
    return await ReadJsonAsync<T>(response);
}

static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
{
    return await response.Content.ReadFromJsonAsync<T>(JsonOptionsHolder.Value)
           ?? throw new InvalidOperationException($"Expected a JSON {typeof(T).Name} response but received an empty body.");
}

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode)
        return;

    throw new InvalidOperationException($"Failed to {operation}: HTTP {(int)response.StatusCode} {response.StatusCode}; {await ReadBodyAsync(response)}");
}

static async Task<string> ReadBodyAsync(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    return body.Length <= 500 ? body : body[..500] + "…";
}

static async Task<Dictionary<Guid, string>> WaitForOrdersToSettleAsync(
    HttpClient gateway,
    string customerToken,
    IReadOnlyCollection<Guid> orderIds,
    TimeSpan timeout)
{
    var statuses = new Dictionary<Guid, string>();
    var deadline = DateTimeOffset.UtcNow.Add(timeout);

    while (DateTimeOffset.UtcNow < deadline)
    {
        var reads = orderIds.Select(async orderId =>
        {
            try
            {
                var order = await GetJsonAsync<OrderView>(gateway, $"/ordering/orders/{orderId}", customerToken, CancellationToken.None);
                return (orderId, Status: order.Status);
            }
            catch (InvalidOperationException)
            {
                // Marten's projection can lag the accepted POST briefly.  The deadline below
                // turns a persistent missing read model into an explicit test failure.
                return (orderId, Status: (string?)null);
            }
        });

        foreach (var (orderId, status) in await Task.WhenAll(reads))
        {
            if (status is not null)
                statuses[orderId] = status;
        }

        if (statuses.Count == orderIds.Count && statuses.Values.All(IsTerminal))
            return statuses;

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    return statuses;
}

static bool IsTerminal(string status) =>
    string.Equals(status, "Confirmed", StringComparison.OrdinalIgnoreCase)
    || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase);

static int CountStatus(IReadOnlyDictionary<string, int> statusCounts, string status) =>
    statusCounts.TryGetValue(status, out var count) ? count : 0;

internal sealed record FlashSaleSettings(
    string GatewayBaseUrl,
    string KeycloakBaseUrl,
    string CustomerUsername,
    string CustomerPassword,
    string OrganizerUsername,
    string OrganizerPassword,
    int SeatCount,
    int VirtualUsers,
    int MaxConnectionsPerServer,
    TimeSpan SettleTimeout,
    string ReportDirectory)
{
    public static FlashSaleSettings Load()
    {
        var seats = GetPositiveInt("FLASH_SALE_SEAT_COUNT", 50);
        var users = GetPositiveInt("FLASH_SALE_VIRTUAL_USERS", 300);
        if (users < seats)
            throw new InvalidOperationException("FLASH_SALE_VIRTUAL_USERS must be at least FLASH_SALE_SEAT_COUNT.");

        return new FlashSaleSettings(
            GetUrl("GATEWAY_BASE_URL", "http://localhost:8080"),
            GetUrl("KEYCLOAK_BASE_URL", "http://localhost:8180"),
            Environment.GetEnvironmentVariable("FLASH_SALE_CUSTOMER_USERNAME") ?? "customer1",
            Require("FLASH_SALE_CUSTOMER_PASSWORD"),
            Environment.GetEnvironmentVariable("FLASH_SALE_ORGANIZER_USERNAME") ?? "organizer1",
            Require("FLASH_SALE_ORGANIZER_PASSWORD"),
            seats,
            users,
            GetPositiveInt("FLASH_SALE_MAX_CONNECTIONS", Math.Min(64, users)),
            // Ticketing renders an actual PDF through LocalStack.  On this laptop 50 durable
            // saga completions can legitimately take several minutes even after the HTTP burst.
            TimeSpan.FromSeconds(GetPositiveInt("FLASH_SALE_SETTLE_TIMEOUT_SECONDS", 600)),
            Environment.GetEnvironmentVariable("FLASH_SALE_REPORT_DIR")
                ?? Path.Combine(AppContext.BaseDirectory, "flash-sale-reports"));
    }

    private static string Require(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable '{variable}' is required and is intentionally not hard-coded.");

    private static int GetPositiveInt(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return int.TryParse(raw, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException($"Environment variable '{variable}' must be a positive integer.");
    }

    private static string GetUrl(string variable, string fallback)
    {
        var value = (Environment.GetEnvironmentVariable(variable) ?? fallback).TrimEnd('/');
        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : throw new InvalidOperationException($"Environment variable '{variable}' must be an absolute URL.");
    }
}

internal sealed record FlashSaleFixture(Guid EventId, IReadOnlyList<Guid> SeatIds);
internal sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
internal sealed record VenueResponse(Guid Id);
internal sealed record EventResponse(Guid Id);
internal sealed record SectionResponse(Guid Id, string Name, int Capacity);
internal sealed record SeatMapResponse(Guid Id, Guid EventId, IReadOnlyList<SectionResponse> Sections);
internal sealed record SeatDto(Guid Id, Guid SectionId, string Label, string Status);
internal sealed record EventAvailabilityResponse(Guid EventId, IReadOnlyList<SeatDto> Seats);
internal sealed record HoldResponse(bool Success, Guid? HoldId, DateTimeOffset? ExpiresAt, IReadOnlyList<Guid> UnavailableSeatIds);
internal sealed record OrderResponse(Guid OrderId, string Status);
internal sealed record OrderView(Guid Id, string Status);
internal sealed record FlashSaleSummary(
    DateTimeOffset CompletedAtUtc,
    string GatewayBaseUrl,
    Guid EventId,
    int SeatCapacity,
    int VirtualUsers,
    int Attempts,
    int HoldsAcquired,
    int SoldOutResponses,
    int OrdersCreated,
    int ConfirmedOrders,
    int CancelledOrders,
    int PendingOrMissingOrders,
    int SeatsSold,
    int UnexpectedFailures,
    double ElapsedMilliseconds,
    IReadOnlyDictionary<string, int> FinalOrderStatuses,
    IReadOnlyList<string> FailureSamples,
    bool Passed);

internal static class JsonOptionsHolder
{
    public static readonly JsonSerializerOptions Value = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
