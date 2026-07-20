using Elastic.Clients.Elasticsearch;
using Scalar.AspNetCore;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Messaging;
using TicketFlow.Observability;
using TicketFlow.Search.Api.Consumers;
using TicketFlow.Search.Api.Endpoints;
using TicketFlow.Search.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(
    builder.Configuration,
    "search",
    configureMetrics: m => m.AddMeter(ContractVersionMetrics.MeterName));
builder.Services.AddOpenApi();
builder.Services.AddSearchInfrastructure(builder.Configuration);

builder.Services.AddTicketFlowMessaging(builder.Configuration, "search", busConfigurator =>
{
    busConfigurator.AddConsumer<CatalogEventPublishedConsumer>();
    busConfigurator.AddConsumer<CatalogEventRescheduledConsumer>();
    busConfigurator.AddConsumer<CatalogEventCancelledConsumer>();
});

var app = builder.Build();

// Dev-only: setup-on-startup, same pattern as EF migrate-on-startup elsewhere in this repo.
if (!app.Environment.IsProduction())
{
    var indexName = builder.Configuration["Elasticsearch:IndexName"] ?? "events";
    using var scope = app.Services.CreateScope();
    var client = scope.ServiceProvider.GetRequiredService<ElasticsearchClient>();
    await ElasticsearchIndexSetup.EnsureCreatedAsync(client, indexName);
}

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapSearchEndpoints();

app.Run();
