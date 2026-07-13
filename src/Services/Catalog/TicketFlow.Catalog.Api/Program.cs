using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Catalog.Api.Endpoints;
using TicketFlow.Catalog.Application;
using TicketFlow.Catalog.Infrastructure;
using TicketFlow.Catalog.Infrastructure.Seeding;
using TicketFlow.Messaging;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddCatalogApplication();
builder.Services.AddCatalogInfrastructure(builder.Configuration);
// Commit 1 (Phase 3): bare bus wiring only, to prove BuildingBlocks.Messaging connects and
// surfaces on /health. No consumers/outbox yet — that lands in Commit 3-4 once the Outbox
// needs to be chained onto this same AddMassTransit call, at which point this likely moves
// into AddCatalogInfrastructure alongside CatalogDbContext.
builder.Services.AddTicketFlowMessaging(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

// Dev-only: auto-migrate + seed on startup. A real deployment would run migrations as a
// separate CI/CD step instead of on every app boot (avoids races across multiple instances).
if (!app.Environment.IsProduction())
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await dbContext.Database.MigrateAsync();
    await CatalogDbSeeder.SeedAsync(dbContext);
}

app.UseTicketFlowWeb();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapVenueEndpoints();
app.MapEventEndpoints();
app.MapSeatMapEndpoints();

app.Run();
