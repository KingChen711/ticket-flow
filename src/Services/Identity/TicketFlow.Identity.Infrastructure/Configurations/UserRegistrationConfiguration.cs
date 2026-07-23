using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Infrastructure.Configurations;

public sealed class UserRegistrationConfiguration : IEntityTypeConfiguration<UserRegistration>
{
    public void Configure(EntityTypeBuilder<UserRegistration> builder)
    {
        builder.ToTable("user_registrations");
        builder.HasKey(registration => registration.Id);
        builder.Property(registration => registration.Id)
            .HasColumnName("registration_key")
            .ValueGeneratedNever();
        builder.Property(registration => registration.NormalizedEmail)
            .IsRequired()
            .HasMaxLength(320);
        builder.HasIndex(registration => registration.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("ux_user_registrations_normalized_email");
        builder.Property(registration => registration.DisplayName)
            .IsRequired()
            .HasMaxLength(200);
        builder.Property(registration => registration.Status)
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(registration => registration.LastError)
            .HasMaxLength(2000);

        // Two retries of the same stable key must re-decide from the winner's checkpoint rather
        // than last-write-wins over each other's external-side-effect progress.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
