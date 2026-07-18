using Scalar.AspNetCore;
using TicketFlow.Messaging;
using TicketFlow.Recommendation.Api.Consumers;
using TicketFlow.Recommendation.Api.Endpoints;
using TicketFlow.Recommendation.Infrastructure;
using TicketFlow.Observability;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(builder.Configuration, "recommendation");
builder.Services.AddOpenApi();
builder.Services.AddRecommendationInfrastructure(builder.Configuration);

builder.Services.AddTicketFlowMessaging(builder.Configuration, "recommendation", busConfigurator =>
{
    busConfigurator.AddConsumer<OrderCompletedConsumer>();
});

var app = builder.Build();

app.UseTicketFlowWeb();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapRecommendationEndpoints();

app.Run();
