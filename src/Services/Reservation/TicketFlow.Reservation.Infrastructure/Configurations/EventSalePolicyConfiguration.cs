using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Reservation.Domain.SalePolicies;

namespace TicketFlow.Reservation.Infrastructure.Configurations;

public sealed class EventSalePolicyConfiguration : IEntityTypeConfiguration<EventSalePolicy>
{
    public void Configure(EntityTypeBuilder<EventSalePolicy> builder)
    {
        builder.ToTable("event_sale_policies");
        builder.HasKey(policy => policy.Id);
        builder.Property(policy => policy.Id).ValueGeneratedNever();
        builder.Property(policy => policy.Status).IsRequired().HasMaxLength(20);
        builder.Property(policy => policy.PriceSnapshotVersion).IsRequired();

        builder.OwnsMany(policy => policy.PriceTiers, tier =>
        {
            tier.ToTable("event_sale_price_tiers");
            tier.WithOwner().HasForeignKey("EventId");
            tier.HasKey(price => price.Id);
            tier.Property(price => price.Id).ValueGeneratedNever();
            tier.Property(price => price.SectionId).IsRequired();
            tier.Property(price => price.Amount).HasColumnType("numeric(12,2)").IsRequired();
            tier.Property(price => price.Currency).HasMaxLength(3).IsRequired();
            tier.HasIndex("EventId", nameof(EventSalePriceTier.SectionId)).IsUnique();
        });
        builder.Navigation(policy => policy.PriceTiers).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
