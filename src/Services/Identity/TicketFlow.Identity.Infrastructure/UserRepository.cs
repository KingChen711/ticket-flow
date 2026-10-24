using Microsoft.EntityFrameworkCore;
using TicketFlow.Identity.Application.Abstractions;
using TicketFlow.Identity.Domain.Users;

namespace TicketFlow.Identity.Infrastructure;

public sealed class UserRepository(IdentityDbContext dbContext) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        dbContext.Users.FirstOrDefaultAsync(u => u.Email.Value == email, cancellationToken);

    public Task<UserRegistration?> GetRegistrationByKeyAsync(
        Guid registrationKey,
        CancellationToken cancellationToken = default) =>
        dbContext.UserRegistrations.FirstOrDefaultAsync(r => r.Id == registrationKey, cancellationToken);

    public Task<UserRegistration?> GetRegistrationByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default) =>
        dbContext.UserRegistrations.FirstOrDefaultAsync(
            r => r.NormalizedEmail == normalizedEmail,
            cancellationToken);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().ToListAsync(cancellationToken);

    public void Add(User user) => dbContext.Users.Add(user);

    public void Add(UserRegistration registration) => dbContext.UserRegistrations.Add(registration);
}
