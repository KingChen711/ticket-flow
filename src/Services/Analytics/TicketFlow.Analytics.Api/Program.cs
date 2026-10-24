using ClickHouse.Client.ADO;
using Confluent.Kafka;
using MassTransit;
using Scalar.AspNetCore;
using TicketFlow.Analytics.Api.Consumers;
using TicketFlow.Analytics.Api.Endpoints;
using TicketFlow.Analytics.Infrastructure;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Messaging;
using TicketFlow.Observability;
using TicketFlow.Web;

const string saleRecordedTopic = "sale-recorded";

var builder = WebApplication.CreateBuilder(args);

if (EfMigrationRunner.IsRequested(args))
{
    var connectionString = builder.Configuration["ClickHouse:ConnectionString"]
        ?? throw new InvalidOperationException("Configuration 'ClickHouse:ConnectionString' was not found.");
    await using var connection = new ClickHouseConnection(connectionString);
    await ClickHouseTableSetup.EnsureCreatedAsync(connection);
    return;
}

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(builder.Configuration, "analytics");
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddAnalyticsInfrastructure(builder.Configuration);

builder.Services.AddTicketFlowMessaging(builder.Configuration, "analytics",
    // Commit 14: the event -> organizer projection that scopes /revenue. Catalog lifecycle facts
    // travel on RabbitMQ, so this is a bus consumer even though sales arrive over Kafka.
    configure: busConfigurator => busConfigurator.AddConsumer<CatalogEventPublishedConsumer>(),
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
if (!app.Environment.IsProduction() &&
    !string.Equals(builder.Configuration["Schema:InitializeOnStartup"], "false", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var connection = scope.ServiceProvider.GetRequiredService<ClickHouseConnection>();
    await ClickHouseTableSetup.EnsureCreatedAsync(connection);
}

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapAnalyticsEndpoints();

app.Run();
