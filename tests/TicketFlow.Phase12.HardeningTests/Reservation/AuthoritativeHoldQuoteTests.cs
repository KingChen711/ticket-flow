using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Domain.SalePolicies;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

public sealed class AuthoritativeHoldQuoteTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-20T08:00:00+00:00");

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Mixed_price_tiers_are_summed_from_server_snapshot_and_bound_to_owner()
    {
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var sectionA = Guid.NewGuid();
        var sectionB = Guid.NewGuid();
        var seats = new[]
        {
            Seat.Create(eventId, sectionA, "A-1"),
            Seat.Create(eventId, sectionB, "B-1"),
        };
        var policy = Policy(eventId, "Published", [(sectionA, 100_000m, "VND"), (sectionB, 150_000m, "VND")]);
        var quotes = new FakeHoldQuoteRepository();
        var service = CreateService(seats, policy, quotes);

        var result = await service.HoldAsync(new HoldSeatsRequest(eventId, seats.Select(seat => seat.Id).ToList(), ownerId));

        Assert.True(result.Success);
        Assert.NotNull(result.Quote);
        Assert.Equal(ownerId, result.Quote.OwnerUserId);
        Assert.Equal(250_000m, result.Quote.Amount);
        Assert.Equal("VND", result.Quote.Currency);
        Assert.Equal(11, result.Quote.PriceSnapshotVersion);
        Assert.Equal(seats.Select(seat => seat.Id), result.Quote.SeatIds);
        Assert.Single(quotes.Quotes);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Cancelled")]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Non_published_event_is_rejected_before_hold_or_quote_persistence(string status)
    {
        var eventId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var seat = Seat.Create(eventId, sectionId, "A-1");
        var quotes = new FakeHoldQuoteRepository();
        var unitOfWork = new ControlledReservationUnitOfWork();
        var service = CreateService([seat], Policy(eventId, status, [(sectionId, 100_000m, "VND")]), quotes, unitOfWork);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HoldAsync(new HoldSeatsRequest(eventId, [seat.Id], Guid.NewGuid())));

        Assert.Empty(quotes.Quotes);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Quote_cannot_be_read_by_another_owner_or_after_event_cancellation()
    {
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var seat = Seat.Create(eventId, sectionId, "A-1");
        var policy = Policy(eventId, "Published", [(sectionId, 100_000m, "VND")]);
        var policyRepository = new FakeEventSalePolicyRepository(policy);
        var quotes = new FakeHoldQuoteRepository();
        var service = CreateService([seat], policy, quotes, policyRepository: policyRepository);
        var held = await service.HoldAsync(new HoldSeatsRequest(eventId, [seat.Id], ownerId));

        Assert.Null(await service.GetQuoteAsync(held.HoldId!.Value, Guid.NewGuid()));

        policy.ApplySnapshot(
            "Cancelled", Now, Now.AddHours(1), 12, Now.AddMinutes(1),
            [(sectionId, 100_000m, "VND")]);

        Assert.Null(await service.GetQuoteAsync(held.HoldId.Value, ownerId));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Existing_hold_keeps_its_price_when_a_newer_snapshot_arrives()
    {
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var seat = Seat.Create(eventId, sectionId, "A-1");
        var policy = Policy(eventId, "Published", [(sectionId, 100_000m, "VND")]);
        var service = CreateService([seat], policy, new FakeHoldQuoteRepository());
        var held = await service.HoldAsync(new HoldSeatsRequest(eventId, [seat.Id], ownerId));

        policy.ApplySnapshot(
            "Published", Now, Now.AddHours(2), 12, Now.AddMinutes(1),
            [(sectionId, 150_000m, "VND")]);

        var originalQuote = await service.GetQuoteAsync(held.HoldId!.Value, ownerId);
        Assert.NotNull(originalQuote);
        Assert.Equal(100_000m, originalQuote.Amount);
        Assert.Equal(11, originalQuote.PriceSnapshotVersion);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Published_event_after_sale_window_is_rejected_before_Redis_hold()
    {
        var eventId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var seat = Seat.Create(eventId, sectionId, "A-1");
        var policy = EventSalePolicy.FromSnapshot(
            eventId,
            "Published",
            Now.AddHours(-2),
            Now.AddMinutes(-1),
            11,
            Now.AddHours(-2),
            [(sectionId, 100_000m, "VND")]);
        var quotes = new FakeHoldQuoteRepository();
        var unitOfWork = new ControlledReservationUnitOfWork();
        var service = CreateService([seat], policy, quotes, unitOfWork);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HoldAsync(new HoldSeatsRequest(eventId, [seat.Id], Guid.NewGuid())));

        Assert.Empty(quotes.Quotes);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    private static ReservationService CreateService(
        IEnumerable<Seat> seats,
        EventSalePolicy policy,
        FakeHoldQuoteRepository quotes,
        ControlledReservationUnitOfWork? unitOfWork = null,
        FakeEventSalePolicyRepository? policyRepository = null) => new(
        new FakeSeatRepository(seats),
        new FakeSeatHoldStore(),
        policyRepository ?? new FakeEventSalePolicyRepository(policy),
        quotes,
        unitOfWork ?? new ControlledReservationUnitOfWork(),
        new ControlledSeatMapNotifier(),
        new FixedTimeProvider(Now),
        new ReservationOptions());

    private static EventSalePolicy Policy(
        Guid eventId,
        string status,
        IEnumerable<(Guid SectionId, decimal Amount, string Currency)> tiers) =>
        EventSalePolicy.FromSnapshot(
            eventId,
            status,
            Now.AddMinutes(-1),
            Now.AddHours(1),
            11,
            Now,
            tiers);
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
