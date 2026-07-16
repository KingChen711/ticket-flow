using ClickHouse.Client.ADO;
using Confluent.Kafka;
using MassTransit;
using Scalar.AspNetCore;
using TicketFlow.Analytics.Api.Consumers;
using TicketFlow.Analytics.Api.Endpoints;
using TicketFlow.Analytics.Api.Messaging;
using TicketFlow.Analytics.Infrastructure;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Messaging;
using TicketFlow.Web;

const string saleRecordedTopic = "sale-recorded";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddAnalyticsInfrastructure(builder.Configuration);

var kafkaHost = builder.Configuration["Kafka:Host"]
                ?? throw new InvalidOperationException("Configuration 'Kafka:Host' was not found.");

// Must run BEFORE AddTicketFlowMessaging — Analytics' TopicEndpoint below would crash bus startup
// if the topic doesn't exist yet (confirmed via a real probe, Commit 6). Idempotent: fine even if
// Ordering already auto-created this topic via its own first Produce().
await KafkaTopicSetup.EnsureCreatedAsync(kafkaHost, saleRecordedTopic);

builder.Services.AddTicketFlowMessaging(builder.Configuration, "analytics",
    configureKafka: rider => { rider.AddConsumer<SaleRecordedConsumer>(); },
    configureKafkaFactory: (context, kafkaConfigurator) =>
    {
        kafkaConfigurator.TopicEndpoint<SaleRecorded>(saleRecordedTopic, "analytics-sale-recorded", endpoint =>
        {
            endpoint.ConfigureConsumer<SaleRecordedConsumer>(context);
            // Replay the full topic on first run — analytics wants the complete sales history,
            // not just what arrives after this consumer group first connects.
            endpoint.AutoOffsetReset = AutoOffsetReset.Earliest;
        });
    });

var app = builder.Build();

// Dev-only: setup-on-startup, same pattern as ElasticsearchIndexSetup/EF migrate-on-startup
// elsewhere in this repo.
if (!app.Environment.IsProduction())
{
    using var scope = app.Services.CreateScope();
    var connection = scope.ServiceProvider.GetRequiredService<ClickHouseConnection>();
    await ClickHouseTableSetup.EnsureCreatedAsync(connection);
}

app.UseTicketFlowWeb();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapAnalyticsEndpoints();

app.Run();
