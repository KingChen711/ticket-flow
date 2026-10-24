using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Tests.Seats;

public sealed class ReservationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _eventId = Guid.NewGuid();
    private readonly FakeSeatHoldStore _holdStore = new();
    private readonly FakeSeatRepository _seatRepository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeSeatMapNotifier _notifier = new();
    private readonly ReservationService _service;
    private readonly IReadOnlyList<Seat> _seats;

    public ReservationServiceTests()
    {
        _seats = SeatInventory.ForSection(_eventId, Guid.NewGuid(), "A", 3);
        _seatRepository.AddRange(_seats);
        _service = new ReservationService(
            _seatRepository, _holdStore, _unitOfWork, _notifier, new FixedTimeProvider(Now),
            new ReservationOptions { HoldTtlMinutes = 10 });
    }

    [Fact]
    public async Task Hold_of_free_seats_succeeds_with_configured_ttl_and_broadcasts_Held()
    {
        var seatIds = Ids(0, 1);

        var result = await _service.HoldAsync(new HoldSeatsRequest(_eventId, seatIds));

        Assert.True(result.Success);
        Assert.NotNull(result.HoldId);
        Assert.Equal(Now.AddMinutes(10), result.ExpiresAt);
        Assert.Equal(TimeSpan.FromMinutes(10), _holdStore.LastTtl);
        var update = Assert.Single(_notifier.Updates);
        Assert.Equal("Held", update.Status);
        Assert.Equal(seatIds, update.SeatIds);
    }

    [Fact]
    public async Task Second_hold_on_an_overlapping_seat_is_rejected_and_names_the_taken_seat()
    {
        await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0)));

        var result = await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0, 1)));

        Assert.False(result.Success);
        Assert.Null(result.HoldId);
        Assert.Equal(Ids(0), result.UnavailableSeatIds);
        Assert.Single(_notifier.Updates);
    }

    [Fact]
    public async Task Hold_without_seats_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.HoldAsync(new HoldSeatsRequest(_eventId, [])));
    }

    [Fact]
    public async Task Confirm_marks_held_seats_sold_in_postgres_and_broadcasts_Sold()
    {
        var hold = await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0, 1)));

        var confirmed = await _service.ConfirmAsync(_eventId, hold.HoldId!.Value);

        Assert.Equal(Ids(0, 1).Order(), confirmed.Order());
        Assert.Equal(SeatStatus.Sold, _seats[0].Status);
        Assert.Equal(SeatStatus.Sold, _seats[1].Status);
        Assert.Equal(SeatStatus.Available, _seats[2].Status);
        Assert.Equal(1, _unitOfWork.SaveCount);
        Assert.Equal("Sold", _notifier.Updates[^1].Status);
    }

    [Fact]
    public async Task Confirm_only_returns_seats_still_owned_by_the_hold()
    {
        var hold = await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0, 1)));
        _holdStore.Expire(_seats[1].Id);

        var confirmed = await _service.ConfirmAsync(_eventId, hold.HoldId!.Value);

        Assert.Equal(Ids(0), confirmed);
        Assert.Equal(SeatStatus.Available, _seats[1].Status);
    }

    [Fact]
    public async Task Confirm_of_unknown_or_already_confirmed_hold_returns_empty_and_writes_nothing()
    {
        var hold = await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0)));
        await _service.ConfirmAsync(_eventId, hold.HoldId!.Value);

        var second = await _service.ConfirmAsync(_eventId, hold.HoldId.Value);
        var unknown = await _service.ConfirmAsync(_eventId, Guid.NewGuid());

        Assert.Empty(second);
        Assert.Empty(unknown);
        Assert.Equal(1, _unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Release_frees_the_seats_for_the_next_buyer()
    {
        var hold = await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0)));

        var released = await _service.ReleaseAsync(_eventId, hold.HoldId!.Value);
        var next = await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0)));

        Assert.True(released);
        Assert.True(next.Success);
        Assert.Contains(_notifier.Updates, update => update.Status == "Available");
    }

    [Fact]
    public async Task Release_of_unknown_hold_returns_false()
    {
        Assert.False(await _service.ReleaseAsync(_eventId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Unsell_reverses_a_confirmed_sale_and_is_idempotent()
    {
        var hold = await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0)));
        await _service.ConfirmAsync(_eventId, hold.HoldId!.Value);

        await _service.UnsellAsync(_eventId, Ids(0));
        await _service.UnsellAsync(_eventId, Ids(0));

        Assert.False(_holdStore.IsSold(_seats[0].Id));
        Assert.Equal(SeatStatus.Available, _seats[0].Status);
        Assert.True((await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0)))).Success);
    }

    [Fact]
    public async Task Availability_overlays_live_hold_and_sold_status_on_postgres_seats()
    {
        var sold = await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0)));
        await _service.ConfirmAsync(_eventId, sold.HoldId!.Value);
        await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(1)));

        var availability = await _service.GetAvailabilityAsync(_eventId);

        var statusById = availability.Seats.ToDictionary(seat => seat.Id, seat => seat.Status);
        Assert.Equal("Sold", statusById[_seats[0].Id]);
        Assert.Equal("Held", statusById[_seats[1].Id]);
        Assert.Equal("Available", statusById[_seats[2].Id]);
    }

    [Fact]
    public async Task CheckAvailability_treats_unknown_and_held_seats_as_unavailable()
    {
        await _service.HoldAsync(new HoldSeatsRequest(_eventId, Ids(0)));
        var madeUpSeatId = Guid.NewGuid();

        var result = await _service.CheckAvailabilityAsync(_eventId, [_seats[0].Id, _seats[2].Id, madeUpSeatId]);

        Assert.False(result.AllAvailable);
        Assert.Equal(new[] { _seats[0].Id, madeUpSeatId }.Order(), result.UnavailableSeatIds.Order());
    }

    [Fact]
    public async Task CheckAvailability_of_free_known_seats_is_all_available()
    {
        var result = await _service.CheckAvailabilityAsync(_eventId, Ids(1, 2));

        Assert.True(result.AllAvailable);
        Assert.Empty(result.UnavailableSeatIds);
    }

    [Fact]
    public async Task SeatSections_returns_only_the_requested_seats_of_this_event()
    {
        var otherEventSeat = SeatInventory.ForSection(Guid.NewGuid(), Guid.NewGuid(), "B", 1)[0];
        _seatRepository.AddRange([otherEventSeat]);

        var sections = await _service.GetSeatSectionsAsync(_eventId, [_seats[0].Id, otherEventSeat.Id, Guid.NewGuid()]);

        var only = Assert.Single(sections);
        Assert.Equal(_seats[0].Id, only.Id);
        Assert.Equal(_seats[0].SectionId, only.SectionId);
    }

    private List<Guid> Ids(params int[] indexes) => indexes.Select(index => _seats[index].Id).ToList();
}
