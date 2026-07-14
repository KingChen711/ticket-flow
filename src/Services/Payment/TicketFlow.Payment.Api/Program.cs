using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Messaging;
using TicketFlow.Payment.Api.Consumers;
using TicketFlow.Payment.Application;
using TicketFlow.Payment.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddOpenApi();
builder.Services.AddPaymentApplication();
builder.Services.AddPaymentInfrastructure(builder.Configuration);
builder.Services.AddTicketFlowMessaging(builder.Configuration, "payment", busConfigurator =>
{
    busConfigurator.AddConsumer<ProcessPaymentConsumer>();
    busConfigurator.AddConsumer<RefundPaymentConsumer>();
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

app.MapOpenApi();
app.MapScalarApiReference();

app.Run();
