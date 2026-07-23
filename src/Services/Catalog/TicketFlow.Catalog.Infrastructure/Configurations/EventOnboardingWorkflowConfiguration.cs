using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Catalog.Domain.Workflows;

namespace TicketFlow.Catalog.Infrastructure.Configurations;

public sealed class EventOnboardingWorkflowConfiguration : IEntityTypeConfiguration<EventOnboardingWorkflow>
{
    public void Configure(EntityTypeBuilder<EventOnboardingWorkflow> builder)
    {
        builder.ToTable("event_onboarding_workflows");
        builder.HasKey(workflow => workflow.Id);
        builder.Property(workflow => workflow.Id).ValueGeneratedNever();
        builder.Property(workflow => workflow.CreationKey).IsRequired().HasMaxLength(128);
        builder.HasIndex(workflow => workflow.CreationKey).IsUnique();
        builder.Property(workflow => workflow.RequestFingerprint).IsRequired().HasMaxLength(64);
        builder.Property(workflow => workflow.VenueName).HasMaxLength(200);
        builder.Property(workflow => workflow.VenueStreet).HasMaxLength(300);
        builder.Property(workflow => workflow.VenueCity).HasMaxLength(150);
        builder.Property(workflow => workflow.VenueCountry).HasMaxLength(150);
        builder.Property(workflow => workflow.VenuePostalCode).HasMaxLength(20);
        builder.Property(workflow => workflow.EventTitle).IsRequired().HasMaxLength(200);
        builder.Property(workflow => workflow.EventDescription).HasMaxLength(2000);
        builder.Property(workflow => workflow.EventArtist).IsRequired().HasMaxLength(200);
        builder.Property(workflow => workflow.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(workflow => workflow.LastCompletedStep).HasConversion<string>().HasMaxLength(32);
        builder.Property(workflow => workflow.FailedStep).HasConversion<string>().HasMaxLength(32);
        builder.Property(workflow => workflow.LastError).HasMaxLength(2000);
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.OwnsMany(workflow => workflow.Sections, section =>
        {
            section.ToTable("event_onboarding_sections");
            section.WithOwner().HasForeignKey("WorkflowId");
            section.HasKey("WorkflowId", nameof(EventOnboardingSection.Position));
            section.Property(item => item.Name).IsRequired().HasMaxLength(150);
            section.Property(item => item.Currency).IsRequired().HasMaxLength(3);
            section.Property(item => item.Price).HasColumnType("numeric(12,2)");
            section.HasIndex(item => item.SectionId).IsUnique();
            section.HasIndex(item => item.PriceTierId).IsUnique();
        });
        builder.Navigation(workflow => workflow.Sections).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
