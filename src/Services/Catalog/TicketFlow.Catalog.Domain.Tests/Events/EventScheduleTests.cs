using TicketFlow.Catalog.Domain.Events;

namespace TicketFlow.Catalog.Domain.Tests.Events;

public class EventScheduleTests
{
    [Fact]
    public void Create_WithEndBeforeStart_Throws()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var end = start.AddHours(-1);

        Assert.Throws<ArgumentOutOfRangeException>(() => EventSchedule.Create(start, end));
    }

    [Fact]
    public void Create_WithEndEqualStart_Throws()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => EventSchedule.Create(start, start));
    }

    [Fact]
    public void IsInPast_WhenStartIsBeforeNow_ReturnsTrue()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var schedule = EventSchedule.Create(start, start.AddHours(2));

        Assert.True(schedule.IsInPast(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsInPast_WhenStartIsAfterNow_ReturnsFalse()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var schedule = EventSchedule.Create(start, start.AddHours(2));

        Assert.False(schedule.IsInPast(DateTimeOffset.UtcNow));
    }
}
