using TicketFlow.Messaging;
using TicketFlow.Notification.Api.Consumers;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowMessaging(builder.Configuration, busConfigurator =>
{
    busConfigurator.AddConsumer<CatalogEventPublishedConsumer>();
});

var app = builder.Build();

app.UseTicketFlowWeb();

app.Run();
