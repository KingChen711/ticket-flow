using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Identity.Api.Endpoints;
using TicketFlow.Identity.Application;
using TicketFlow.Identity.Infrastructure;
using TicketFlow.Observability;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

if (EfMigrationRunner.IsRequested(args))
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' was not found.");
    await using var dbContext = IdentityDbContextFactory.Create(connectionString);
    await EfMigrationRunner.RunAsync(dbContext, "identity");
    return;
}

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(builder.Configuration, "identity");
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddIdentityApplication();
builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.AddDbContextReadinessCheck<IdentityDbContext>("postgres");
var keycloakHealthUrl = builder.Configuration["Keycloak:HealthUrl"];
if (Uri.TryCreate(keycloakHealthUrl, UriKind.Absolute, out var keycloakHealthUri))
{
    if (builder.Environment.IsProduction() && keycloakHealthUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("Production requires HTTPS for Keycloak:HealthUrl.");
    builder.Services.AddHttpReadinessCheck("keycloak", keycloakHealthUri);
}
else if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException("Production requires an absolute 'Keycloak:HealthUrl'.");
}
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("registration", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

var app = builder.Build();

// Dev-only: auto-migrate on startup. A real deployment would run migrations as a separate
// CI/CD step instead of on every app boot (avoids races across multiple instances).
if (!app.Environment.IsProduction() &&
    !string.Equals(builder.Configuration["Database:MigrateOnStartup"], "false", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapUserEndpoints();

app.Run();
