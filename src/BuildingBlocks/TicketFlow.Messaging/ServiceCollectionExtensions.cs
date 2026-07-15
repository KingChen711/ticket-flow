using MassTransit;
using MassTransit.RabbitMqTransport;
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
    ///
    /// `serviceName` prefixes the endpoint (queue) name formatter. Without it, MassTransit's
    /// default KebabCaseEndpointNameFormatter derives the queue name purely from the consumer
    /// class name — two services with a same-named consumer class (e.g. two
    /// CatalogEventPublishedConsumer, one per service, Phase 3 Commit 5/6) would collide onto
    /// the SAME RabbitMQ queue and become competing consumers instead of each getting its own
    /// copy of the message, silently breaking fan-out. Caught via docker-first verification:
    /// RabbitMQ Management API showed "consumers": 2 on a single queue instead of 2 queues.
    ///
    /// `configureKafka` is opt-in Kafka Rider wiring (Phase 6 Commit 6) — attaches alongside the
    /// RabbitMQ transport on the same bus, not a replacement. RabbitMQ stays the default for
    /// every service's commands/sagas (Phase 6 mục 3); Kafka Rider is only for the one analytics
    /// stream (SaleRecorded) that actually needs stream/replay semantics. Confirmed via a real
    /// probe against a live Kafka broker before wiring this in: a producer-only Rider
    /// (AddProducer, no AddConsumer/TopicEndpoint) auto-creates its topic on first Produce() —
    /// no admin pre-step needed for producer-only callers like Ordering. A consumer that
    /// subscribes via TopicEndpoint to a topic that doesn't exist yet crashes bus startup
    /// entirely, so any future consumer-side caller (Commit 7's analytics-service) will need its
    /// own explicit topic-creation-on-startup step before calling this.
    /// </summary>
    public static IServiceCollection AddTicketFlowMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<IBusRegistrationConfigurator>? configure = null,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureRabbitMq = null,
        Action<IRiderRegistrationConfigurator>? configureKafka = null)
    {
        var host = configuration["RabbitMq:Host"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Host' was not found.");
        var username = configuration["RabbitMq:Username"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Username' was not found.");
        var password = configuration["RabbitMq:Password"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Password' was not found.");

        services.AddMassTransit(busConfigurator =>
        {
            busConfigurator.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(serviceName, includeNamespace: false));

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

                // Runs BEFORE ConfigureEndpoints so a caller can hand-configure specific
                // consumers' endpoints (e.g. Payment's Commit 7 needs UseEntityFrameworkOutbox on
                // ProcessPaymentConsumer/RefundPaymentConsumer's endpoints for MassTransit Inbox
                // deduplication — not expressible through the generic `configure` callback above,
                // which only sees IBusRegistrationConfigurator, not the RabbitMq-specific
                // configurator). ConfigureEndpoints below skips any consumer that already has an
                // endpoint from here, and still auto-configures everything else by convention.
                configureRabbitMq?.Invoke(context, rabbitConfigurator);

                rabbitConfigurator.ConfigureEndpoints(context);
            });

            if (configureKafka is not null)
            {
                var kafkaHost = configuration["Kafka:Host"]
                    ?? throw new InvalidOperationException("Configuration 'Kafka:Host' was not found.");

                busConfigurator.AddRider(riderConfigurator =>
                {
                    configureKafka(riderConfigurator);
                    riderConfigurator.UsingKafka((_, kafkaConfigurator) => kafkaConfigurator.Host(kafkaHost));
                });
            }
        });

        return services;
    }
}
