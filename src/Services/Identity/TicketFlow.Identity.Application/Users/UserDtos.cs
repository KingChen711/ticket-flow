namespace TicketFlow.Identity.Application.Users;

public sealed record RegisterUserRequest(string Email, string DisplayName, string Password);

public sealed record AuthenticateRequest(string Email, string Password);

public sealed record UserResponse(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles);
