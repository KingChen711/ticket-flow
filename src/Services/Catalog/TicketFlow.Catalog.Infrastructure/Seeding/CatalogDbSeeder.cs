using Microsoft.EntityFrameworkCore;
using TicketFlow.Catalog.Domain.Events;
using TicketFlow.Catalog.Domain.Venues;

namespace TicketFlow.Catalog.Infrastructure.Seeding;

/// <summary>
/// Dev-only sample data so the CRUD endpoints have something to return on a fresh database.
/// Skips entirely if any venue already exists — safe to run on every startup.
/// </summary>
public static class CatalogDbSeeder
{
    public static async Task SeedAsync(CatalogDbContext dbContext, CancellationToken cancellationToken = default)
    {
        if (await dbContext.Venues.AnyAsync(cancellationToken))
            return;

        var phuTho = Venue.Register(
            "Phu Tho Stadium",
            Address.Create("1 Le Dai Hanh", "Ho Chi Minh City", "Vietnam", "700000"));

        var myDinh = Venue.Register(
            "My Dinh National Stadium",
            Address.Create("Le Duc Tho", "Hanoi", "Vietnam", "100000"));

        var hoaBinh = Venue.Register(
            "Hoa Binh Theatre",
            Address.Create("240 Ba Thang Hai", "Ho Chi Minh City", "Vietnam", "700000"));

        dbContext.Venues.AddRange(phuTho, myDinh, hoaBinh);

        var now = DateTimeOffset.UtcNow;

        var blackpink = Event.Create(
            "Born Pink Tour - Ho Chi Minh",
            "BLACKPINK world tour stop in Ho Chi Minh City.",
            "BLACKPINK",
            phuTho.Id,
            EventSchedule.Create(now.AddDays(60), now.AddDays(60).AddHours(3)));

        var soncatrang = Event.Create(
            "Anh Trai Vuot Ngan Chong Gai - Hanoi",
            "Reality-show concert tour finale in Hanoi.",
            "Various Artists",
            myDinh.Id,
            EventSchedule.Create(now.AddDays(45), now.AddDays(45).AddHours(4)));

        var trinhCongSonNight = Event.Create(
            "Trinh Cong Son Tribute Night",
            "An evening of Trinh Cong Son's music.",
            "Various Artists",
            hoaBinh.Id,
            EventSchedule.Create(now.AddDays(20), now.AddDays(20).AddHours(2)));

        dbContext.Events.AddRange(blackpink, soncatrang, trinhCongSonNight);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
