using TicketFlow.Messaging;
using TicketFlow.Notification.Api.Consumers;
using TicketFlow.Notification.Application;
using TicketFlow.Notification.Infrastructure;
using TicketFlow.Observability;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(builder.Configuration, "notification");
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection("Reminder"));
builder.Services.AddNotificationInfrastructure(builder.Configuration, builder.Environment);
var deliveryHealthUri = TicketFlow.Notification.Infrastructure.ServiceCollectionExtensions
    .ResolveDeliveryHealthEndpoint(
    builder.Configuration,
    builder.Environment.EnvironmentName);
if (deliveryHealthUri is not null)
    builder.Services.AddHttpReadinessCheck("notification-provider", deliveryHealthUri);
builder.Services.AddTicketFlowMessaging(builder.Configuration, "notification", busConfigurator =>
{
    busConfigurator.AddConsumer<CatalogEventPublishedConsumer>();
    busConfigurator.AddConsumer<CatalogEventRescheduledConsumer>();
    busConfigurator.AddConsumer<CatalogEventCancelledConsumer>();
    busConfigurator.AddConsumer<NotifyOrderCompletedConsumer>();
    busConfigurator.AddConsumer<SendUpcomingEventRemindersConsumer>();
    busConfigurator.AddConsumer<NotifyOrderCancelledConsumer>();
});

var app = builder.Build();

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.Run();
