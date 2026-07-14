using TicketFlow.Messaging;
using TicketFlow.Search.Api.Consumers;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowMessaging(builder.Configuration, "search", busConfigurator =>
{
    busConfigurator.AddConsumer<CatalogEventPublishedConsumer>();
});

var app = builder.Build();

app.UseTicketFlowWeb();

app.Run();
