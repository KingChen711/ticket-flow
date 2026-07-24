using Scalar.AspNetCore;
using TicketFlow.Messaging;
using TicketFlow.Observability;
using TicketFlow.Recommendation.Api.Consumers;
using TicketFlow.Recommendation.Api.Endpoints;
using TicketFlow.Recommendation.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
// Commit 14: this service validates its own token now. It held none before, so its only
// protection was the gateway route in front of it — exactly the "trust the network" boundary
// quyết định 8 rules out.
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddTicketFlowObservability(builder.Configuration, "recommendation");
builder.Services.AddOpenApi();
builder.Services.AddRecommendationInfrastructure(builder.Configuration);

builder.Services.AddTicketFlowMessaging(builder.Configuration, "recommendation", busConfigurator =>
{
    busConfigurator.AddConsumer<OrderCompletedConsumer>();
});

var app = builder.Build();

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapRecommendationEndpoints();

app.Run();
