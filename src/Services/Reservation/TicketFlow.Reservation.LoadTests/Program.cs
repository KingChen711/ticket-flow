using System.Net;
using System.Net.Http.Json;
using NBomber.CSharp;

// Throughput/latency measurement under a flash-sale-shaped load — a COMPANION to Commit 10's
// Testcontainers test, not a replacement for it. Commit 10 proves correctness (exactly one
// winner per seat) deterministically and fast, in-process against Redis directly. This project
// proves the same invariant still holds when going through the real HTTP API under load, and
// reports how fast the system can process holds — a different kind of evidence, see
// docs/phase-2-plan.md.
//
// Target a running Reservation.Api (defaults to the Docker container's exposed port).
var baseUrl = Environment.GetEnvironmentVariable("RESERVATION_BASE_URL") ?? "http://localhost:8003";
const int seatPoolSize = 100;

var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 500 };
using var http = new HttpClient(handler);
http.BaseAddress = new Uri(baseUrl);

// --- Setup: provision a fresh event with a small pool of seats — small enough, relative to
// the injected request rate below, to guarantee real contention (a "hot" flash sale). ---
var eventId = Guid.NewGuid();
var sectionId = Guid.NewGuid();

var provisionResponse = await http.PostAsJsonAsync(
    $"/events/{eventId}/inventory",
    new { sections = new[] { new { sectionId, sectionName = "GA", capacity = seatPoolSize } } });
provisionResponse.EnsureSuccessStatusCode();

var seedAvailability = await http.GetFromJsonAsync<AvailabilityResponse>($"/events/{eventId}/availability")
                       ?? throw new InvalidOperationException("Failed to read seed inventory.");
var seatIds = seedAvailability.Seats.Select(seat => seat.Id).ToArray();

Console.WriteLine($"Target: {baseUrl}");
Console.WriteLine($"Provisioned event {eventId} with {seatIds.Length} seats.");

// --- Scenario: many virtual users race for a random seat from the pool, confirm on success.
// Once the pool sells out, later requests correctly land on already-sold seats and get 409s —
// that's expected, not a failure. ---
var scenario = Scenario.Create("hold_and_confirm_random_seat", async _ =>
    {
        var seatId = seatIds[Random.Shared.Next(seatIds.Length)];

        var holdResponse = await http.PostAsJsonAsync("/holds", new { eventId, seatIds = new[] { seatId } });

        if (holdResponse.StatusCode == HttpStatusCode.Conflict)
            return Response.Ok(statusCode: "409_seat_taken");

        holdResponse.EnsureSuccessStatusCode();
        var hold = await holdResponse.Content.ReadFromJsonAsync<HoldResponse>();

        var confirmResponse = await http.PostAsJsonAsync($"/holds/{hold!.HoldId}/confirm", new { eventId });
        confirmResponse.EnsureSuccessStatusCode();

        return Response.Ok();
    })
    .WithLoadSimulations(
        // 50 req/s for 20s = ~1000 attempts against a 100-seat pool — 10x oversubscription,
        // same shape as scope.md's "flash sale" scenario (mục 11), just measured over HTTP
        // instead of proven in-process (that's Commit 10's job).
        Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(20))
    );

NBomberRunner
    .RegisterScenarios(scenario)
    // Absolute path anchored to the built assembly's own folder (already under bin/, already
    // gitignored) — a relative path here resolves against the CWD at `dotnet run` time, which
    // is the repo root when run via `dotnet run --project ...`, littering the repo root.
    .WithReportFolder(Path.Combine(AppContext.BaseDirectory, "load-test-reports"))
    .Run();

// --- Post-run correctness check: total sold must never exceed the provisioned pool. This is
// the same invariant Commit 10 proves at unit scale, re-checked here at load-test scale. ---
var finalAvailability = await http.GetFromJsonAsync<AvailabilityResponse>($"/events/{eventId}/availability")
                        ?? throw new InvalidOperationException("Failed to read final inventory.");
var soldCount = finalAvailability.Seats.Count(seat => seat.Status == "Sold");

Console.WriteLine();
Console.WriteLine($"Seats provisioned : {seatIds.Length}");
Console.WriteLine($"Seats sold        : {soldCount}");
Console.WriteLine(soldCount <= seatIds.Length
    ? "PASS — no oversell under load."
    : $"FAIL — oversold by {soldCount - seatIds.Length} seat(s)!");

internal sealed record SeatDto(Guid Id, Guid SectionId, string Label, string Status);

internal sealed record AvailabilityResponse(Guid EventId, List<SeatDto> Seats);

internal sealed record HoldResponse(
    bool Success,
    Guid? HoldId,
    DateTimeOffset? ExpiresAt,
    List<Guid> UnavailableSeatIds);
