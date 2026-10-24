using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Catalog.Domain.Workflows;

namespace TicketFlow.Catalog.Infrastructure.Configurations;

public sealed class EventCancellationWorkflowConfiguration : IEntityTypeConfiguration<EventCancellationWorkflow>
{
    public void Configure(EntityTypeBuilder<EventCancellationWorkflow> builder)
    {
        builder.ToTable("event_cancellation_workflows");
        builder.HasKey(workflow => workflow.Id);
        builder.Property(workflow => workflow.Id).ValueGeneratedNever();
        builder.Property(workflow => workflow.EventId).IsRequired();
        builder.HasIndex(workflow => workflow.EventId).IsUnique();
        builder.Property(workflow => workflow.Reason).IsRequired().HasMaxLength(1000);
        builder.Property(workflow => workflow.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
