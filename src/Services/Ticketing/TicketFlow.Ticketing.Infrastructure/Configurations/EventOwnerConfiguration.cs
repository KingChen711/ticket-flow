using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure.Configurations;

public sealed class EventOwnerConfiguration : IEntityTypeConfiguration<EventOwner>
{
    public void Configure(EntityTypeBuilder<EventOwner> builder)
    {
        builder.ToTable("event_owners");

        // EventId is the key, not a surrogate: this is a projection of one fact per event, so a
        // redelivered CatalogEventPublished has to land on the same row rather than add another.
        builder.HasKey(o => o.EventId);
        builder.Property(o => o.EventId).ValueGeneratedNever();
        builder.Property(o => o.OrganizerId).IsRequired();
        builder.Property(o => o.Revision).IsRequired();
        builder.Property(o => o.UpdatedAt).IsRequired();
    }
}
