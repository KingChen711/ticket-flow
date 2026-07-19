using MassTransit;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Messaging;
using TicketFlow.Payment.Api.Consumers;
using TicketFlow.Payment.Application;
using TicketFlow.Payment.Infrastructure;
using TicketFlow.Observability;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
// Commit 5, mục 3 rủi ro số 3 — "Polly" is Polly.Extensions' own Meter name (not configurable),
// turned on by PaymentGatewayResiliencePipeline's .ConfigureTelemetry(...) call. Same
// configureMetrics escape hatch Ordering uses for its own ticketflow.tickets.sold Meter.
builder.Services.AddTicketFlowObservability(
    builder.Configuration,
    "payment",
    configureMetrics: m => m.AddMeter("Polly"));
builder.Services.AddOpenApi();
builder.Services.AddPaymentApplication();
builder.Services.AddPaymentInfrastructure(builder.Configuration);
builder.Services.AddTicketFlowMessaging(
    builder.Configuration,
    "payment",
    configure: busConfigurator =>
    {
        // AddEntityFrameworkOutbox registers the EF Core Inbox/Outbox machinery on the bus (both
        // sides share this one call) — UsePostgres/UseBusOutbox mirror Catalog's Commit 3 wiring.
        busConfigurator.AddEntityFrameworkOutbox<PaymentDbContext>(outboxConfigurator =>
        {
            outboxConfigurator.UsePostgres();
            outboxConfigurator.UseBusOutbox();
        });

        busConfigurator.AddConsumer<ProcessPaymentConsumer>();
        busConfigurator.AddConsumer<RefundPaymentConsumer>();
    },
    configureRabbitMq: (context, rabbitConfigurator) =>
    {
        // Manual endpoints (not ConfigureEndpoints' convention) — only way to attach
        // UseEntityFrameworkOutbox (which is what actually turns on Inbox deduplication) to a
        // SPECIFIC consumer's receive endpoint. Names match exactly what the KebabCase formatter
        // would have produced automatically (verified in Commit 2's Docker logs), so this is a
        // continuation of the existing queues, not a rename.
        rabbitConfigurator.ReceiveEndpoint("payment-process-payment", e =>
        {
            // One Postgres connection is held while the EF inbox/outbox transaction runs.
            // Keep the burst consumer below Payment's bounded pool so flash-sale messages queue
            // in RabbitMQ instead of exhausting PostgreSQL or timing out in Npgsql.
            e.ConcurrentMessageLimit = 8;
            e.UseEntityFrameworkOutbox<PaymentDbContext>(context);
            e.ConfigureConsumer<ProcessPaymentConsumer>(context);
        });

        rabbitConfigurator.ReceiveEndpoint("payment-refund-payment", e =>
        {
            e.ConcurrentMessageLimit = 8;
            e.UseEntityFrameworkOutbox<PaymentDbContext>(context);
            e.ConfigureConsumer<RefundPaymentConsumer>(context);
        });
    });

var app = builder.Build();

// Dev-only: auto-migrate on startup, same pattern as Catalog/Identity/Reservation.
if (!app.Environment.IsProduction())
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapOpenApi();
app.MapScalarApiReference();

app.Run();
