using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Infrastructure.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(u => u.PasswordHash).IsRequired();

        builder.OwnsOne(u => u.Email, email =>
        {
            email.Property(e => e.Value).HasColumnName("email").IsRequired().HasMaxLength(320);
            email.HasIndex(e => e.Value).IsUnique();
        });
        builder.Navigation(u => u.Email).IsRequired();

        // Roles is a small, order-insensitive enum collection — stored as a comma-separated
        // string rather than EF Core's newer "primitive collection" mapping to keep this
        // commit on well-trodden ground (HasConversion has been stable since EF Core 2).
        builder.Property(u => u.Roles)
            .HasField("_roles")
            .HasColumnName("roles")
            .HasConversion(
                roles => string.Join(',', roles),
                value => (IReadOnlyList<Role>)(value.Length == 0
                    ? new List<Role>()
                    : value.Split(',').Select(Enum.Parse<Role>).ToList()),
                new ValueComparer<IReadOnlyList<Role>>(
                    (a, b) => a!.SequenceEqual(b!),
                    a => a.Aggregate(0, (hash, role) => HashCode.Combine(hash, role)),
                    a => a.ToList()));

        builder.Ignore(u => u.DomainEvents);
    }
}
