using TicketFlow.Catalog.Domain.Events;

namespace TicketFlow.Catalog.Domain.Tests.Events;

public class EventTests
{
    private static readonly Guid VenueId = Guid.NewGuid();

    private static EventSchedule FutureSchedule(DateTimeOffset? now = null)
    {
        var start = (now ?? DateTimeOffset.UtcNow).AddDays(7);
        return EventSchedule.Create(start, start.AddHours(3));
    }

    private static EventSchedule PastSchedule(DateTimeOffset? now = null)
    {
        var start = (now ?? DateTimeOffset.UtcNow).AddDays(-7);
        return EventSchedule.Create(start, start.AddHours(3));
    }

    private static Event NewDraftEvent() =>
        Event.Create("Coldplay Live", "Music of the Spheres Tour", "Coldplay", VenueId, FutureSchedule());

    [Fact]
    public void Create_WithValidData_RaisesEventCreated()
    {
        var @event = NewDraftEvent();

        var raised = Assert.Single(@event.DomainEvents.OfType<EventCreated>());
        Assert.Equal(@event.Id, raised.CatalogEventId);
        Assert.Equal("Coldplay Live", raised.Title);
        Assert.Equal(VenueId, raised.VenueId);
        Assert.Equal(EventStatus.Draft, @event.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Create_WithEmptyTitle_Throws(string? title)
    {
        Assert.Throws<ArgumentException>(() => Event.Create(title!, "desc", "Artist", VenueId, FutureSchedule()));
    }

    [Fact]
    public void AddPriceTier_WhileDraft_Succeeds()
    {
        var @event = NewDraftEvent();

        @event.AddPriceTier(Guid.NewGuid(), Money.Create(500_000));

        Assert.Single(@event.PriceTiers);
    }

    [Fact]
    public void AddPriceTier_WithDuplicateSection_Throws()
    {
        var @event = NewDraftEvent();
        var sectionId = Guid.NewGuid();
        @event.AddPriceTier(sectionId, Money.Create(500_000));

        Assert.Throws<InvalidOperationException>(() => @event.AddPriceTier(sectionId, Money.Create(600_000)));
    }

    [Fact]
    public void AddPriceTier_WithEmptySectionId_Throws()
    {
        var @event = NewDraftEvent();

        Assert.Throws<ArgumentException>(() => @event.AddPriceTier(Guid.Empty, Money.Create(500_000)));
    }

    [Fact]
    public void Publish_WithoutPriceTier_Throws()
    {
        var @event = NewDraftEvent();

        Assert.Throws<InvalidOperationException>(() => @event.Publish(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Publish_WithPriceTierAndFutureSchedule_RaisesEventPublished()
    {
        var @event = NewDraftEvent();
        @event.AddPriceTier(Guid.NewGuid(), Money.Create(500_000));

        @event.Publish(DateTimeOffset.UtcNow);

        Assert.Equal(EventStatus.Published, @event.Status);
        Assert.Contains(@event.DomainEvents, e => e is EventPublished);
    }

    [Fact]
    public void Publish_ScheduledInThePast_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var @event = Event.Create("Old Show", "desc", "Artist", VenueId, PastSchedule(now));
        @event.AddPriceTier(Guid.NewGuid(), Money.Create(500_000));

        Assert.Throws<InvalidOperationException>(() => @event.Publish(now));
    }

    [Fact]
    public void Publish_WhenAlreadyPublished_Throws()
    {
        var @event = NewDraftEvent();
        @event.AddPriceTier(Guid.NewGuid(), Money.Create(500_000));
        @event.Publish(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => @event.Publish(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Reschedule_ToPastDate_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var @event = NewDraftEvent();

        Assert.Throws<InvalidOperationException>(() => @event.Reschedule(PastSchedule(now), now));
    }

    [Fact]
    public void Reschedule_ToFutureDate_RaisesEventRescheduled()
    {
        var now = DateTimeOffset.UtcNow;
        var @event = NewDraftEvent();
        var newSchedule = FutureSchedule(now.AddDays(1));

        @event.Reschedule(newSchedule, now);

        Assert.Equal(newSchedule, @event.Schedule);
        Assert.Contains(@event.DomainEvents, e => e is EventRescheduled);
    }

    [Fact]
    public void Reschedule_AfterCancelled_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var @event = NewDraftEvent();
        @event.Cancel();

        Assert.Throws<InvalidOperationException>(() => @event.Reschedule(FutureSchedule(now), now));
    }

    [Fact]
    public void Cancel_RaisesEventCancelled()
    {
        var @event = NewDraftEvent();

        @event.Cancel();

        Assert.Equal(EventStatus.Cancelled, @event.Status);
        Assert.Contains(@event.DomainEvents, e => e is EventCancelled);
    }

    [Fact]
    public void Cancel_WhenAlreadyCancelled_Throws()
    {
        var @event = NewDraftEvent();
        @event.Cancel();

        Assert.Throws<InvalidOperationException>(() => @event.Cancel());
    }
}
