using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;
using Scalar.AspNetCore;
using TicketFlow.Messaging;
using TicketFlow.Ticketing.Api.Consumers;
using TicketFlow.Ticketing.Application;
using TicketFlow.Ticketing.Infrastructure;
using TicketFlow.Web;

// QuestPDF Community license — free for this learning project (see
// Directory.Packages.props comment). Set once, globally, at startup.
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddOpenApi();
builder.Services.AddTicketingApplication();
builder.Services.AddTicketingInfrastructure(builder.Configuration);
builder.Services.AddTicketFlowMessaging(builder.Configuration, "ticketing", busConfigurator =>
{
    busConfigurator.AddConsumer<IssueTicketsConsumer>();
});

var app = builder.Build();

// Dev-only: auto-migrate on startup, same pattern as Catalog/Identity/Reservation/Payment.
if (!app.Environment.IsProduction())
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseTicketFlowWeb();

app.MapOpenApi();
app.MapScalarApiReference();

app.Run();
