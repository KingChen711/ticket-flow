using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow.Messaging;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// RabbitMQ transport wiring shared by every service. MassTransit registers its own
    /// IHealthCheck implementations automatically as part of AddMassTransit — no hand-rolled
    /// health check needed here (unlike Redis in Reservation.Infrastructure, which had no
    /// built-in ASP.NET Core integration to piggyback on). Callers must have already called
    /// services.AddHealthChecks() (done by TicketFlow.Web.AddTicketFlowWeb()) for those checks
    /// to surface on /health.
    ///
    /// The optional `configure` callback is where each service registers its own consumers —
    /// kept generic here so this extension doesn't need to know about any service's contracts.
    /// </summary>
    public static IServiceCollection AddTicketFlowMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configure = null)
    {
        var host = configuration["RabbitMq:Host"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Host' was not found.");
        var username = configuration["RabbitMq:Username"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Username' was not found.");
        var password = configuration["RabbitMq:Password"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Password' was not found.");

        services.AddMassTransit(busConfigurator =>
        {
            busConfigurator.SetKebabCaseEndpointNameFormatter();

            configure?.Invoke(busConfigurator);

            busConfigurator.UsingRabbitMq((context, rabbitConfigurator) =>
            {
                rabbitConfigurator.Host(host, "/", hostConfigurator =>
                {
                    hostConfigurator.Username(username);
                    hostConfigurator.Password(password);
                });

                rabbitConfigurator.UseMessageRetry(retryConfigurator =>
                    retryConfigurator.Interval(3, TimeSpan.FromSeconds(2)));

                rabbitConfigurator.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
