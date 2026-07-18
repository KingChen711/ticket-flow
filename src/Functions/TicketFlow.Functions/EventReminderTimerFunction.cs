using Amazon.Lambda.Core;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketFlow.Contracts.Messaging.Notification;
using TicketFlow.Messaging;

namespace TicketFlow.Functions;

/// <summary>
/// EventBridge-triggered, thin by design (docs/phase-9-plan.md mục 5) — the only job here is to
/// publish SendUpcomingEventReminders onto RabbitMQ; every "who needs reminding" decision lives in
/// notification-service's SendUpcomingEventRemindersConsumer, which owns that data. Reuses
/// AddTicketFlowMessaging (same retry/endpoint-naming config every other service gets) via a bare
/// Microsoft.Extensions.Hosting generic host — this Lambda has no ASP.NET Core builder of its own
/// to hang DI off of (AWSProjectType=Lambda is a plain class library), so a minimal host is built
/// once per execution environment instead, same "static readonly, built once" pattern
/// S3ClientFactory/MinioClientFactory already use for their clients. RabbitMQ reachability from a
/// Lambda executor container on this network was already confirmed in Commit 1
/// (nc -zv rabbitmq 5672 succeeded).
///
/// Host.CreateApplicationBuilder() reads environment variables into IConfiguration the same way
/// WebApplication.CreateBuilder() does — RabbitMq:Host/Username/Password come from
/// RabbitMq__Host/RabbitMq__Username/RabbitMq__Password env vars set on this function's
/// definition (double-underscore is ASP.NET Core config's env-var section-nesting convention),
/// same MINIO_*-style env-var injection 03-deploy-ticketing.sh already established for
/// RenderTicketPdfFunction.
/// </summary>
public sealed class EventReminderTimerFunction
{
    private static readonly IHost Host = BuildHost();

    public async Task<string> Handler(System.Text.Json.JsonElement scheduledEvent, ILambdaContext context)
    {
        var triggeredAt = DateTimeOffset.UtcNow;
        var publishEndpoint = Host.Services.GetRequiredService<IPublishEndpoint>();

        await publishEndpoint.Publish(new SendUpcomingEventReminders(triggeredAt));

        var message = $"EventReminderTimer published SendUpcomingEventReminders at {triggeredAt:O}, request id {context.AwsRequestId}";
        context.Logger.LogInformation(message);
        return message;
    }

    private static IHost BuildHost()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddTicketFlowMessaging(builder.Configuration, "event-reminder-timer");

        var host = builder.Build();
        host.Start();
        return host;
    }
}
