using TicketFlow.Identity.Api.Security;
using TicketFlow.Identity.Application.Users;

namespace TicketFlow.Identity.Api.Endpoints;

public sealed record TokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/dev-token", async (
            AuthenticateRequest request,
            UserService userService,
            DevTokenGenerator tokenGenerator,
            CancellationToken ct) =>
        {
            var user = await userService.AuthenticateAsync(request, ct);
            if (user is null)
                return Results.Unauthorized();

            var (accessToken, expiresAt) = tokenGenerator.GenerateToken(user);
            return Results.Ok(new TokenResponse(accessToken, "Bearer", expiresAt));
        }).WithTags("Auth");
    }
}
