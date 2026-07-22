using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Reservation.Api.Endpoints;
using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.SalePolicies;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

public sealed class HoldEndpointSecurityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-22T08:00:00+00:00");

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Authenticated_user_cannot_confirm_a_hold_directly_over_HTTP()
    {
        var fixture = await CreateHeldFixtureAsync();
        await using var app = await StartAppAsync(fixture.Service);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", fixture.OwnerUserId.ToString());

        using var response = await client.PostAsJsonAsync(
            $"/holds/{fixture.HoldId}/confirm",
            new { eventId = fixture.EventId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints),
            endpoint => endpoint is RouteEndpoint route
                && string.Equals(
                    route.RoutePattern.RawText,
                    "/holds/{holdId:guid}/confirm",
                    StringComparison.OrdinalIgnoreCase)
                && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                    .Contains(HttpMethods.Post, StringComparer.OrdinalIgnoreCase) == true);
        Assert.Equal(HoldQuoteState.Active, fixture.Quote.State);
        Assert.Equal(SeatStatus.Available, fixture.Seat.Status);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Release_endpoint_cannot_release_another_users_hold()
    {
        var fixture = await CreateHeldFixtureAsync();
        await using var app = await StartAppAsync(fixture.Service);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Sub", Guid.NewGuid().ToString());

        using var response = await client.PostAsync($"/holds/{fixture.HoldId}/release", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HoldQuoteState.Active, fixture.Quote.State);
        Assert.NotNull(await fixture.Service.GetQuoteAsync(fixture.HoldId, fixture.OwnerUserId));
    }

    private static async Task<HeldFixture> CreateHeldFixtureAsync()
    {
        var eventId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var seat = Seat.Create(eventId, sectionId, "SEC-1");
        var policy = EventSalePolicy.FromSnapshot(
            eventId,
            "Published",
            Now.AddMinutes(-1),
            Now.AddHours(1),
            12,
            Now,
            [(sectionId, 125_000m, "VND")]);
        var quotes = new FakeHoldQuoteRepository();
        var service = new ReservationService(
            new FakeSeatRepository([seat]),
            new FakeSeatHoldStore(),
            new FakeEventSalePolicyRepository(policy),
            quotes,
            new FakeSeatAllocationRepository(),
            new FakeReservationOperationReceiptRepository(),
            new FakeAllocationGenerationSource(),
            new ControlledReservationUnitOfWork(),
            new RecordingSeatAvailabilityEventPublisher(),
            new FixedTimeProvider(Now),
            new ReservationOptions());

        var held = await service.HoldAsync(new HoldSeatsRequest(eventId, [seat.Id], ownerUserId));
        var holdId = Assert.IsType<Guid>(held.HoldId);
        var quote = Assert.Single(quotes.Quotes);
        return new HeldFixture(service, eventId, ownerUserId, holdId, seat, quote);
    }

    private static async Task<WebApplication> StartAppAsync(ReservationService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("RequireAuthenticated", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(service);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Sub", out var subject))
            {
                context.User = new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim("sub", subject.ToString())], "HardeningTest"));
            }

            await next(context);
        });
        app.UseAuthorization();
        app.MapHoldEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed record HeldFixture(
        ReservationService Service,
        Guid EventId,
        Guid OwnerUserId,
        Guid HoldId,
        Seat Seat,
        HoldQuote Quote);
}
