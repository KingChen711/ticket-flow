using MassTransit;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Messaging;
using TicketFlow.Observability;
using TicketFlow.Ticketing.Api.Consumers;
using TicketFlow.Ticketing.Api.Endpoints;
using TicketFlow.Ticketing.Application;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

if (EfMigrationRunner.IsRequested(args))
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' was not found.");
    await using var dbContext = TicketingDbContextFactory.Create(connectionString);
    await EfMigrationRunner.RunAsync(dbContext, "ticketing");
    return;
}

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(
    builder.Configuration,
    "ticketing",
    configureMetrics: m => m
        .AddMeter(ContractVersionMetrics.MeterName)
        // Commit 10: a transient issuance fault is silent by design (it throws and is retried),
        // so without this a renderer failing every time looks like one that works.
        .AddMeter(TicketIssuanceMetrics.MeterName));
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddTicketingApplication();
builder.Services.AddTicketingInfrastructure(builder.Configuration);
builder.Services.AddDbContextReadinessCheck<TicketingDbContext>("postgres");
RegisterHttpDependency("Aws:HealthUrl", "functions-provider", requiredInProduction: true);
RegisterHttpDependency("Minio:HealthUrl", "minio", requiredInProduction: true);
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

        // Commit 10: a refunded order's tickets must stop working. Before this nothing ever
        // withdrew one — the money went back and the seat was given away too.
        busConfigurator.AddConsumer<RevokeTicketsConsumer>();

        // Commit 14: keeps the event -> organizer projection a gate scan is authorized against.
        busConfigurator.AddConsumer<CatalogEventPublishedConsumer>();
    },
    configureRabbitMq: (context, rabbitConfigurator) =>
    {
        // Manual endpoint (see Payment's Commit 7 doc comment for why) — name matches exactly
        // what the KebabCase formatter already produces (confirmed in Commit 3/4 Docker logs),
        // so this is a continuation of the existing queue, not a rename.
        rabbitConfigurator.ReceiveEndpoint("ticketing-issue-tickets", e =>
        {
            e.SetQuorumQueue();
            // PDF rendering invokes LocalStack's Docker-backed Lambda executor.  On this local
            // Compose host, parallel cold starts can time out before the function begins; a
            // single durable consumer lets RabbitMQ absorb a flash-sale fan-out instead of
            // turning a temporary executor-capacity limit into cancelled orders.
            e.ConcurrentMessageLimit = 1;
            e.UseEntityFrameworkOutbox<TicketingDbContext>(context);
            e.ConfigureConsumer<IssueTicketsConsumer>(context);
        });
    });

var app = builder.Build();

// Dev-only: auto-migrate on startup, same pattern as Catalog/Identity/Reservation/Payment.
if (!app.Environment.IsProduction() &&
    !string.Equals(builder.Configuration["Database:MigrateOnStartup"], "false", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapTicketEndpoints();

app.Run();

void RegisterHttpDependency(string configurationKey, string name, bool requiredInProduction)
{
    var value = builder.Configuration[configurationKey];
    if (Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
    {
        if (builder.Environment.IsProduction() && endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Production requires HTTPS for '{configurationKey}'.");
        builder.Services.AddHttpReadinessCheck(name, endpoint);
    }
    else if (requiredInProduction && builder.Environment.IsProduction())
    {
        throw new InvalidOperationException($"Production requires an absolute '{configurationKey}'.");
    }
}
