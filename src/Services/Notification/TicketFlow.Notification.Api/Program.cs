using TicketFlow.Messaging;
using TicketFlow.Notification.Api.Consumers;
using TicketFlow.Notification.Application;
using TicketFlow.Notification.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection("Reminder"));
builder.Services.AddNotificationInfrastructure(builder.Configuration);
builder.Services.AddTicketFlowMessaging(builder.Configuration, "notification", busConfigurator =>
{
    busConfigurator.AddConsumer<CatalogEventPublishedConsumer>();
    busConfigurator.AddConsumer<CatalogEventRescheduledConsumer>();
    busConfigurator.AddConsumer<CatalogEventCancelledConsumer>();
    busConfigurator.AddConsumer<NotifyOrderCompletedConsumer>();
    busConfigurator.AddConsumer<SendUpcomingEventRemindersConsumer>();
});

var app = builder.Build();

app.UseTicketFlowWeb();

app.Run();
