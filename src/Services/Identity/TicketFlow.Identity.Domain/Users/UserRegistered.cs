using TicketFlow.Domain;

namespace TicketFlow.Identity.Domain.Users;

public sealed record UserRegistered(Guid UserId, string Email, Role Role) : DomainEvent;
