using MassTransit;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Messaging;
using TicketFlow.Ticketing.Api.Consumers;
using TicketFlow.Ticketing.Api.Endpoints;
using TicketFlow.Ticketing.Application;
using TicketFlow.Ticketing.Infrastructure;
using TicketFlow.Observability;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(builder.Configuration, "ticketing");
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddTicketingApplication();
builder.Services.AddTicketingInfrastructure(builder.Configuration);
builder.Services.AddTicketFlowMessaging(
    builder.Configuration,
    "ticketing",
    configure: busConfigurator =>
    {
        busConfigurator.AddEntityFrameworkOutbox<TicketingDbContext>(outboxConfigurator =>
        {
            outboxConfigurator.UsePostgres();
            outboxConfigurator.UseBusOutbox();
        });

        busConfigurator.AddConsumer<IssueTicketsConsumer>();
    },
    configureRabbitMq: (context, rabbitConfigurator) =>
    {
        // Manual endpoint (see Payment's Commit 7 doc comment for why) — name matches exactly
        // what the KebabCase formatter already produces (confirmed in Commit 3/4 Docker logs),
        // so this is a continuation of the existing queue, not a rename.
        rabbitConfigurator.ReceiveEndpoint("ticketing-issue-tickets", e =>
        {
            e.UseEntityFrameworkOutbox<TicketingDbContext>(context);
            e.ConfigureConsumer<IssueTicketsConsumer>(context);
        });
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

app.MapTicketEndpoints();

app.Run();
