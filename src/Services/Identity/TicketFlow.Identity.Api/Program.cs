using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Identity.Api.Endpoints;
using TicketFlow.Identity.Application;
using TicketFlow.Identity.Infrastructure;
using TicketFlow.Observability;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(builder.Configuration, "identity");
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddIdentityApplication();
builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

// Dev-only: auto-migrate on startup. A real deployment would run migrations as a separate
// CI/CD step instead of on every app boot (avoids races across multiple instances).
if (!app.Environment.IsProduction())
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseTicketFlowWeb();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapUserEndpoints();

app.Run();
