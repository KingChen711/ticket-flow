using TicketFlow.Messaging;
using TicketFlow.Recommendation.Api.Consumers;
using TicketFlow.Recommendation.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddRecommendationInfrastructure(builder.Configuration);

builder.Services.AddTicketFlowMessaging(builder.Configuration, "recommendation", busConfigurator =>
{
    busConfigurator.AddConsumer<OrderCompletedConsumer>();
});

var app = builder.Build();

app.UseTicketFlowWeb();

app.Run();
