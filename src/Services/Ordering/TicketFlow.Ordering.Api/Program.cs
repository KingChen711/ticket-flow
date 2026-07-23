using MassTransit;
using Scalar.AspNetCore;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Contracts.Reservation;
using TicketFlow.Messaging;
using TicketFlow.Observability;
using TicketFlow.Ordering.Api.Consumers;
using TicketFlow.Ordering.Api.Endpoints;
using TicketFlow.Ordering.Api.Messaging;
using TicketFlow.Ordering.Api.Metrics;
using TicketFlow.Ordering.Api.Services;
using TicketFlow.Ordering.Application;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Checkout;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Application.Sagas;
using TicketFlow.Ordering.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddSingleton<TicketSalesMetrics>();
builder.Services.AddTicketFlowObservability(
    builder.Configuration,
    "ordering",
    configureMetrics: m => m
        .AddMeter(TicketSalesMetrics.MeterName)
        .AddMeter(ContractVersionMetrics.MeterName)
        // Commit 7: a replayed checkout and a backlogged outbox are both invisible in logs.
        .AddMeter(CheckoutOperationMetrics.MeterName)
        // Commit 8: an ignored milestone and a re-decided version race are both silent by design.
        .AddMeter(OrderMilestoneMetrics.MeterName)
        // Commit 9: a silent participant produces no log line of its own.
        .AddMeter(PurchaseSagaMetrics.MeterName));
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddOrderingApplication();
builder.Services.AddOrderingInfrastructure(builder.Configuration);
builder.Services.AddSingleton(new OrderDispatchOptions());
builder.Services.AddHostedService<OrderDispatchRelay>();
builder.Services.AddScoped<IHoldQuoteProvider, GrpcHoldQuoteProvider>();
builder.Services.AddScoped<ISaleRecordedPublisher, KafkaSaleRecordedPublisher>();

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
    // PurchaseSagaDefinition partitions every message type by OrderId and buffers outgoing
    // messages until Marten has committed the saga version. MartenRepository enables optimistic
    // concurrency for saga documents itself; the explicit callback used here before Commit 8 was
    // redundant and, crucially, could not stop a losing execution publishing before SaveChanges.
    cfg.AddSagaStateMachine<PurchaseSaga, PurchaseState, PurchaseSagaDefinition>()
        .MartenRepository();

    // ONE consumer for every lifecycle milestone (Commit 8), still separate from the saga:
    // orchestration and audit trail remain two concerns, but the audit trail now has a single
    // writer. The six per-message consumers this replaces each owned a queue, so six of them
    // could be inside one order's stream at the same time.
    cfg.AddConsumer<OrderMilestoneConsumer>();

    // Commit 9: keeps the Kafka analytics write off the saga's critical path.
    cfg.AddConsumer<SaleRelayConsumer>();
}, configureKafka: rider =>
{
    // Producer-only — Ordering never consumes from Kafka. Confirmed via a real probe: this
    // auto-creates the "sale-recorded" topic on first Produce(), no admin pre-step needed here
    // (unlike a consumer subscribing via TopicEndpoint, which would need one).
    rider.AddProducer<SaleRecorded>("sale-recorded");
});

var app = builder.Build();

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapCheckSeatsEndpoint();
app.MapOrderEndpoints();
app.MapOperatorEndpoints();

app.Run();
