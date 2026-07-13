using Microsoft.EntityFrameworkCore;
using TicketFlow.Catalog.Api.Endpoints;
using TicketFlow.Catalog.Application;
using TicketFlow.Catalog.Infrastructure;
using TicketFlow.Catalog.Infrastructure.Seeding;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddCatalogApplication();
builder.Services.AddCatalogInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "TicketFlow Catalog API",
        Version = "v1"
    });
});

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

app.UseSwagger();
app.UseSwaggerUI();

app.MapVenueEndpoints();
app.MapEventEndpoints();
app.MapSeatMapEndpoints();

app.Run();
