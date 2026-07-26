using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Catalog.Api.Endpoints;
using TicketFlow.Catalog.Application;
using TicketFlow.Catalog.Infrastructure;
using TicketFlow.Catalog.Infrastructure.Seeding;
using TicketFlow.Observability;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

if (EfMigrationRunner.IsRequested(args))
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' was not found.");
    await using var dbContext = CatalogDbContextFactory.Create(connectionString);
    await EfMigrationRunner.RunAsync(dbContext, "catalog");
    return;
}

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(builder.Configuration, "catalog");
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddCatalogApplication();
// Messaging (bus + EF Core Outbox) wired inside AddCatalogInfrastructure — the outbox has to
// share CatalogDbContext with the business writes for SaveChangesAsync to cover both in one
// transaction (Phase 3 Commit 3).
builder.Services.AddCatalogInfrastructure(builder.Configuration);
builder.Services.AddDbContextReadinessCheck<CatalogDbContext>("postgres");
builder.Services.AddOpenApi();

var app = builder.Build();

// Dev-only: auto-migrate + seed on startup. A real deployment would run migrations as a
// separate CI/CD step instead of on every app boot (avoids races across multiple instances).
if (!app.Environment.IsProduction() &&
    !string.Equals(builder.Configuration["Database:MigrateOnStartup"], "false", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await dbContext.Database.MigrateAsync();
    await CatalogDbSeeder.SeedAsync(dbContext);
}

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapVenueEndpoints();
app.MapEventEndpoints();
app.MapSeatMapEndpoints();
app.MapCatalogAdminEndpoints();
app.MapWorkflowEndpoints();

app.Run();
