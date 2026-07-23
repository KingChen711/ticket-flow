using TicketFlow.Identity.Application.Users;

namespace TicketFlow.Identity.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/users").WithTags("Users");

        app.MapPost("/register", async (RegisterUserRequest request, UserService service, CancellationToken ct) =>
        {
            var user = await service.RegisterAsync(request, ct);
            return Results.Created($"/users/{user.Id}", user);
        }).WithTags("Users").RequireRateLimiting("registration");

        group.MapGet("/", async (UserService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct))).RequireAuthorization("RequireAdmin");

        group.MapGet("/{id:guid}", async (Guid id, UserService service, CancellationToken ct) =>
        {
            var user = await service.GetByIdAsync(id, ct);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }).RequireAuthorization("RequireAdmin");
    }
}
