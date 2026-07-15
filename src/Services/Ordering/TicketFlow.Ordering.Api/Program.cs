using MassTransit;
using Scalar.AspNetCore;
using TicketFlow.Contracts.Reservation;
using TicketFlow.Messaging;
using TicketFlow.Ordering.Api.Consumers;
using TicketFlow.Ordering.Api.Endpoints;
using TicketFlow.Ordering.Application;
using TicketFlow.Ordering.Application.Sagas;
using TicketFlow.Ordering.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddOpenApi();
builder.Services.AddOrderingApplication();
builder.Services.AddOrderingInfrastructure(builder.Configuration);

builder.Services.AddGrpcClient<ReservationService.ReservationServiceClient>(options =>
{
    var address = builder.Configuration["Reservation:GrpcAddress"] ?? "http://localhost:9003";
    options.Address = new Uri(address);
});

builder.Services.AddTicketFlowMessaging(builder.Configuration, "ordering", cfg =>
{
    // No connection string here — reuses the IDocumentStore already registered by
    // AddOrderingInfrastructure's AddMarten(...) call (same Postgres as the Order event store).
    // The connectionString-accepting overload still compiles but is obsolete as of this
    // MassTransit.Marten version precisely because it used to spin up a second, redundant
    // DocumentStore instead of sharing the one DI already has.
    cfg.AddSagaStateMachine<PurchaseSaga, PurchaseState>()
        .MartenRepository();

    // Order-stream milestone consumers (Commit 5) — separate from the saga, see
    // OrderSeatsConfirmedConsumer's doc comment for why these aren't folded into PurchaseSaga.
    cfg.AddConsumer<OrderSeatsConfirmedConsumer>();
    cfg.AddConsumer<OrderPaymentAuthorizedConsumer>();
    cfg.AddConsumer<OrderTicketsIssuedConsumer>();
    cfg.AddConsumer<OrderCompletedConsumer>();

    // Compensation milestone consumers (Commit 9).
    cfg.AddConsumer<OrderPaymentRefundedConsumer>();
    cfg.AddConsumer<OrderSeatsReleasedConsumer>();
    cfg.AddConsumer<OrderCancelledConsumer>();
});

var app = builder.Build();

app.UseTicketFlowWeb();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapCheckSeatsEndpoint();
app.MapOrderEndpoints();

app.Run();
