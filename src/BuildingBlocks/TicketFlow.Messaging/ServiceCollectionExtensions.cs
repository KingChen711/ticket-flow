using System.Security.Authentication;
using Confluent.Kafka;
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
    /// probe against a live Kafka broker before wiring this in: both producer and consumer can
    /// lose their startup race when topic auto-creation is disabled. Phase 12 therefore provisions
    /// topics once through an idempotent Helm/Compose job, then every Kafka workload gates startup
    /// on that topic contract. Application replicas never compete to mutate broker metadata.
    ///
    /// `configureKafkaFactory` is the Kafka-specific analog of `configureRabbitMq` — same reason
    /// it exists: `configureKafka` only sees IRiderRegistrationConfigurator (AddProducer/
    /// AddConsumer registration), not IKafkaFactoryConfigurator, which is where TopicEndpoint
    /// (consumer subscription) actually lives. Runs after `.Host(...)` is set.
    /// </summary>
    public static IServiceCollection AddTicketFlowMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<IBusRegistrationConfigurator>? configure = null,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureRabbitMq = null,
        Action<IRiderRegistrationConfigurator>? configureKafka = null,
        Action<IRiderRegistrationContext, IKafkaFactoryConfigurator>? configureKafkaFactory = null)
    {
        var host = configuration["RabbitMq:Host"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Host' was not found.");
        var username = configuration["RabbitMq:Username"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Username' was not found.");
        var password = configuration["RabbitMq:Password"]
            ?? throw new InvalidOperationException("Configuration 'RabbitMq:Password' was not found.");
        var virtualHost = configuration["RabbitMq:VirtualHost"] ?? "/";
        var rabbitUseSsl = bool.TryParse(configuration["RabbitMq:UseSsl"], out var configuredRabbitSsl)
            && configuredRabbitSsl;
        var isProduction = string.Equals(
            configuration["ASPNETCORE_ENVIRONMENT"],
            "Production",
            StringComparison.OrdinalIgnoreCase);

        if (isProduction && !rabbitUseSsl)
            throw new InvalidOperationException("Production RabbitMQ transport requires RabbitMq:UseSsl=true.");

        if (isProduction && virtualHost == "/")
            throw new InvalidOperationException("Production RabbitMQ transport requires a dedicated RabbitMq:VirtualHost.");

        services.AddMassTransit(busConfigurator =>
        {
            busConfigurator.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(serviceName, includeNamespace: false));

            // Durable is MassTransit's default, and quorum makes the persistence contract
            // explicit at the broker. On the local one-node chart this has one member; the same
            // declaration automatically replicates when production RabbitMQ has more nodes.
            busConfigurator.AddConfigureEndpointsCallback((_, endpointConfigurator) =>
            {
                if (endpointConfigurator is IRabbitMqReceiveEndpointConfigurator rabbitEndpoint)
                    rabbitEndpoint.SetQuorumQueue();
            });

            // Commit 9: backs the saga deadlines, the missing-instance redelivery and the
            // delayed-redelivery ladder below. The RabbitMQ delayed-message-exchange plugin does
            // the scheduling, so a pending timeout survives a process restart — an in-memory
            // scheduler would lose every outstanding deadline on deploy, precisely when a stuck
            // saga needs one.
            //
            // The plugin is NOT in the stock rabbitmq:3-management image; docker-compose.infra.yml
            // and the Helm values therefore run masstransit/rabbitmq, which is that image with this
            // one plugin enabled. Verified against a live broker: without it the early-milestone
            // redelivery silently does nothing and the message is dropped exactly as before.
            busConfigurator.AddDelayedMessageScheduler();

            configure?.Invoke(busConfigurator);

            busConfigurator.UsingRabbitMq((context, rabbitConfigurator) =>
            {
                rabbitConfigurator.Host(host, virtualHost, hostConfigurator =>
                {
                    hostConfigurator.Username(username);
                    hostConfigurator.Password(password);

                    if (rabbitUseSsl)
                    {
                        hostConfigurator.UseSsl(sslConfigurator =>
                        {
                            sslConfigurator.Protocol = SslProtocols.Tls12;
                            sslConfigurator.ServerName = configuration["RabbitMq:ServerName"] ?? host;
                        });
                    }
                });

                rabbitConfigurator.UseDelayedMessageScheduler();

                // Commit 9. Order matters: UseDelayedRedelivery must be configured BEFORE
                // UseMessageRetry so the fast in-process retries are exhausted first and only then
                // is the message scheduled back onto the broker.
                //
                // An out-of-order message is not a fault to retry hard — it is early. It gets the
                // long scheduled ladder (5s → 5m) and is excluded from the fast one, because six
                // seconds of immediate retries could never let the milestone it waits for arrive,
                // and the old flat 3×2s therefore sent it straight to _error for a manual redrive.
                // A permanent fault is excluded from both: retrying it only delays the DLQ entry an
                // operator needs in order to notice.
                rabbitConfigurator.UseDelayedRedelivery(redelivery =>
                {
                    redelivery.Ignore<PermanentMessageFaultException>();
                    redelivery.Intervals(TicketFlowFaultPolicy.DelayedRedeliveryIntervals);
                });

                rabbitConfigurator.UseMessageRetry(retryConfigurator =>
                {
                    retryConfigurator.Ignore<PermanentMessageFaultException>();
                    retryConfigurator.Ignore<OutOfOrderMessageException>();
                    retryConfigurator.Intervals(TicketFlowFaultPolicy.ImmediateRetryIntervals);
                });

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
                var kafkaUseSsl = bool.TryParse(configuration["Kafka:UseSsl"], out var configuredKafkaSsl)
                    && configuredKafkaSsl;
                var kafkaUseSasl = bool.TryParse(configuration["Kafka:UseSasl"], out var configuredKafkaSasl)
                    && configuredKafkaSasl;

                if (isProduction && (!kafkaUseSsl || !kafkaUseSasl))
                {
                    throw new InvalidOperationException(
                        "Production Kafka transport requires Kafka:UseSsl=true and Kafka:UseSasl=true.");
                }

                busConfigurator.AddRider(riderConfigurator =>
                {
                    configureKafka(riderConfigurator);
                    riderConfigurator.UsingKafka((context, kafkaConfigurator) =>
                    {
                        // librdkafka keeps reconnecting, but every delay is bounded and jittered.
                        // Topic creation is deliberately disabled broker-side and belongs to the
                        // Helm provisioning Job, never to an application replica.
                        kafkaConfigurator.ReconnectBackoff = TimeSpan.FromMilliseconds(
                            int.TryParse(configuration["Kafka:ReconnectBackoffMs"], out var reconnectBackoff)
                                ? reconnectBackoff
                                : 250);
                        kafkaConfigurator.ReconnectBackoffMax = TimeSpan.FromMilliseconds(
                            int.TryParse(configuration["Kafka:ReconnectBackoffMaxMs"], out var reconnectBackoffMax)
                                ? reconnectBackoffMax
                                : 10_000);
                        kafkaConfigurator.Host(kafkaHost, hostConfigurator =>
                        {
                            if (kafkaUseSsl)
                            {
                                hostConfigurator.UseSsl(sslConfigurator =>
                                {
                                    var caLocation = configuration["Kafka:CaLocation"];
                                    if (!string.IsNullOrWhiteSpace(caLocation))
                                        sslConfigurator.CaLocation = caLocation;
                                });
                            }

                            if (kafkaUseSasl)
                            {
                                var kafkaUsername = configuration["Kafka:Username"]
                                    ?? throw new InvalidOperationException("Configuration 'Kafka:Username' was not found.");
                                var kafkaPassword = configuration["Kafka:Password"]
                                    ?? throw new InvalidOperationException("Configuration 'Kafka:Password' was not found.");
                                var mechanismText = configuration["Kafka:SaslMechanism"] ?? "ScramSha512";
                                if (!Enum.TryParse<SaslMechanism>(mechanismText, ignoreCase: true, out var mechanism))
                                {
                                    throw new InvalidOperationException(
                                        $"Unsupported Kafka:SaslMechanism '{mechanismText}'.");
                                }

                                hostConfigurator.UseSasl(saslConfigurator =>
                                {
                                    saslConfigurator.Username = kafkaUsername;
                                    saslConfigurator.Password = kafkaPassword;
                                    saslConfigurator.Mechanism = mechanism;
                                    saslConfigurator.SecurityProtocol = kafkaUseSsl
                                        ? SecurityProtocol.SaslSsl
                                        : SecurityProtocol.SaslPlaintext;
                                });
                            }
                        });

                        configureKafkaFactory?.Invoke(context, kafkaConfigurator);
                    });
                });
            }
        });

        return services;
    }
}
