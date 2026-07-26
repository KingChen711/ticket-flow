using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Infrastructure.Configurations;

public sealed class GateAssignmentConfiguration : IEntityTypeConfiguration<GateAssignment>
{
    public void Configure(EntityTypeBuilder<GateAssignment> builder)
    {
        builder.ToTable("gate_assignments");
        builder.HasKey(assignment => new
        {
            assignment.EventId,
            assignment.GateId,
            assignment.UserId,
        });

        builder.Property(assignment => assignment.EventId).ValueGeneratedNever();
        builder.Property(assignment => assignment.GateId)
            .HasMaxLength(GateAssignment.MaxGateIdLength)
            .IsRequired();
        builder.Property(assignment => assignment.UserId).ValueGeneratedNever();
        builder.Property(assignment => assignment.AssignedBy).IsRequired();
        builder.Property(assignment => assignment.AssignedAt).IsRequired();

        builder.HasIndex(assignment => assignment.UserId);
    }
}
