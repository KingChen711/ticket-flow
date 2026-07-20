using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Catalog.Domain.Events;

namespace TicketFlow.Catalog.Infrastructure.Configurations;

public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Title).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.Artist).IsRequired().HasMaxLength(200);
        builder.Property(e => e.VenueId).IsRequired();
        builder.HasIndex(e => e.OrganizerId);
        builder.Property(e => e.AggregateVersion).HasDefaultValue(0L).IsRequired();
        builder.Property(e => e.PriceSnapshotVersion).HasDefaultValue(0L).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        // PostgreSQL's system xmin supplies optimistic concurrency without an application column.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.OwnsOne(e => e.Schedule, schedule =>
        {
            schedule.Property(s => s.StartsAt).HasColumnName("schedule_starts_at").IsRequired();
            schedule.Property(s => s.EndsAt).HasColumnName("schedule_ends_at").IsRequired();
        });
        builder.Navigation(e => e.Schedule).IsRequired();

        builder.OwnsMany(e => e.PriceTiers, priceTier =>
        {
            priceTier.ToTable("event_price_tiers");
            priceTier.WithOwner().HasForeignKey("EventId");
            priceTier.HasKey(p => p.Id);
            priceTier.Property(p => p.Id).ValueGeneratedNever();
            priceTier.Property(p => p.SectionId).IsRequired();

            priceTier.OwnsOne(p => p.Price, money =>
            {
                money.Property(m => m.Amount).HasColumnName("amount").HasColumnType("numeric(12,2)").IsRequired();
                money.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            });
        });
        builder.Navigation(e => e.PriceTiers).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(e => e.DomainEvents);
    }
}
