using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using Elastic.Clients.Elasticsearch;
using Marten;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Analytics;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Sagas;
using MongoDB.Driver;
using Testcontainers.Elasticsearch;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using TicketFlow.Notification.Infrastructure;
using TicketFlow.Search.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class HardeningEnvironmentCollection : ICollectionFixture<HardeningEnvironmentFixture>
{
    public const string Name = "Phase 12 real infrastructure";
}

/// <summary>
/// One real dependency set for the explicit Phase 12 gate: Postgres is shared by Marten and EF
/// tests, RabbitMQ carries the real PurchaseSaga, and Redis exercises cache connectivity. The
/// images deliberately match docker-compose.infra.yml instead of introducing a test-only stack.
/// </summary>
public sealed class HardeningEnvironmentFixture : IAsyncLifetime
{
    // max_connections raised from the default 100, but only as far as actually needed. Every suite
    // here shares this one server and ConcurrentCheckInTests alone holds 100 connections open at
    // its barrier (that concurrency IS the invariant under test, per the plan's "100 scan đồng
    // thời"), so the default ceiling is hit by that test plus anything beside it — surfacing as
    // Postgres 53300 "sorry, too many clients already". 150 covers it with headroom; Postgres
    // allocates per-connection memory up front, and this fixture runs alongside Elasticsearch on a
    // developer machine, so a bigger number is memory spent for nothing.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithCommand("-c", "max_connections=150")
        .Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    // Elasticsearch and MongoDB live in this same fixture rather than a second container-owning
    // one (Commit 11). Two fixtures meant two collections creating containers concurrently, and
    // that combination crashed the test host while each collection passed cleanly on its own —
    // one owner, one startup, no race. Security off and a small heap both match what these tests
    // need: docker-compose.infra.yml runs Elasticsearch unsecured too, and its 1g heap is sized
    // for also ingesting Serilog from 11 processes, which these tests never do.
    private readonly ElasticsearchContainer _elasticsearch = new ElasticsearchBuilder()
        .WithImage("docker.elastic.co/elasticsearch/elasticsearch:8.15.3")
        .WithEnvironment("xpack.security.enabled", "false")
        // 256m heap and a hard 1GB container ceiling. Testcontainers defaults Elasticsearch to a
        // 2g heap, which on a 15GB machine already giving Docker 7GB left the host itself short
        // and got the test host killed outright — no exception, no dump, which is what an
        // OS-level kill looks like. These tests index a handful of documents; compose needs more
        // only because that node also ingests Serilog from 11 processes.
        .WithEnvironment("ES_JAVA_OPTS", "-Xms256m -Xmx256m")
        .WithEnvironment("xpack.ml.enabled", "false")
        .WithCreateParameterModifier(parameters => parameters.HostConfig.Memory = 1024L * 1024 * 1024)
        // Explicit readiness check, replacing the module's default. Testcontainers.Elasticsearch
        // is built around its own default image (8.6.1) with security ON, and waits for a log line
        // that combination emits; 8.15.3 with security off never prints it, so StartAsync simply
        // never returns — observed as a run sitting past 15 minutes with all five containers up,
        // healthy, and completely idle. Waiting on an actual answered HTTP request is also the
        // more honest gate: what these tests need is a node that serves requests, not a node that
        // logged a particular sentence.
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(9200).ForPath("/_cluster/health")))
        .Build();

    private readonly MongoDbContainer _mongo = new MongoDbBuilder().WithImage("mongo:7").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("masstransit/rabbitmq:latest").Build();

    private ServiceProvider? _provider;
    private IBusControl? _bus;

    public string PostgresConnectionString => _postgres.GetConnectionString();
    public string RedisConnectionString => _redis.GetConnectionString();
    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();
    public ElasticEventSearchIndex SearchIndex { get; private set; } = null!;
    public MongoEventReminderReadModel ReminderReadModel { get; private set; } = null!;

    // Raw handles below the ports, so a test can write a document in the shape a PRE-Commit-11
    // producer left behind — no revision/tombstoned field at all. The ports themselves can no
    // longer produce that shape, so there is no way to set up the rolling-upgrade case through
    // them.
    public ElasticsearchClient SearchClient { get; private set; } = null!;
    public string SearchIndexName => "phase12-search-durability";
    public IMongoDatabase ReminderDatabase { get; private set; } = null!;
    public IMongoDatabase RecommendationDatabase { get; private set; } = null!;
    public IDocumentStore MartenStore => _provider!.GetRequiredService<IDocumentStore>();
    public IPublishEndpoint PublishEndpoint => _bus!;
    public MessageProbeSink Messages => _provider!.GetRequiredService<MessageProbeSink>();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _postgres.StartAsync(), _redis.StartAsync(), _rabbitMq.StartAsync(),
            _elasticsearch.StartAsync(), _mongo.StartAsync());

        // Built by hand rather than via GetConnectionString(): the module returns
        // "https://elastic:<password>@host:port" because it assumes the security it configures by
        // default is on. This node has it off, so it speaks plain HTTP, and the client's TLS
        // handshake fails with "Cannot determine the frame size or a corrupted frame was
        // received" — an error that reads like a broken node rather than a wrong scheme.
        SearchClient = new ElasticsearchClient(
            new ElasticsearchClientSettings(
                    new Uri($"http://{_elasticsearch.Hostname}:{_elasticsearch.GetMappedPublicPort(9200)}"))
                .DefaultIndex(SearchIndexName));
        await ElasticsearchIndexSetup.EnsureCreatedAsync(SearchClient, SearchIndexName);
        SearchIndex = new ElasticEventSearchIndex(SearchClient, SearchIndexName);

        // Notification's own composition root calls this before building a client
        // (ServiceCollectionExtensions.AddNotificationInfrastructure); MongoDB.Driver v3 dropped
        // the implicit Guid representation, so without it every Guid-typed filter throws. Skipping
        // it here would mean testing a Mongo client configured differently from the one that runs
        // — the same fixture/production divergence that hid two real bugs in Commit 8 and 9.
        MongoSerializationConfig.EnsureConfigured();
        var mongoClient = new MongoClient(_mongo.GetConnectionString());
        ReminderDatabase = mongoClient.GetDatabase("phase12_notification_durability");
        RecommendationDatabase = mongoClient.GetDatabase("phase12_recommendation_concurrency");
        ReminderReadModel = new MongoEventReminderReadModel(ReminderDatabase);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<MessageProbeSink>();
        services.AddSingleton<ISaleRecordedPublisher, RecordingSaleRecordedPublisher>();

        services.AddMarten(options =>
        {
            options.Connection(PostgresConnectionString);
            options.DatabaseSchemaName = "phase12_hardening";
        });

        services.AddMassTransit(configuration =>
        {
            configuration.SetEndpointNameFormatter(
                new KebabCaseEndpointNameFormatter("phase12-hardening", includeNamespace: false));

            // Keep the real-infrastructure fixture on the exact production partition/outbox and
            // Marten repository configuration; otherwise the permutation gate measures a bus
            // topology that Ordering never runs.
            configuration.AddSagaStateMachine<PurchaseSaga, PurchaseState, PurchaseSagaDefinition>()
                .MartenRepository();

            configuration.AddConsumer<InfrastructureProbeConsumer>();
            configuration.AddConsumer<ConfirmSeatsProbeConsumer>();
            configuration.AddConsumer<ProcessPaymentProbeConsumer>();
            configuration.AddConsumer<IssueTicketsProbeConsumer>();
            configuration.AddConsumer<OrderCompletedProbeConsumer>();

            // Must mirror AddTicketFlowMessaging: the deadline and the missing-instance redelivery
            // both schedule through this, so a fixture without it would test a bus the services
            // never run — the same divergence that already hid two real bugs in this suite.
            configuration.AddDelayedMessageScheduler();

            configuration.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(new Uri(RabbitMqConnectionString));
                rabbit.UseDelayedMessageScheduler();
                rabbit.ConfigureEndpoints(context);
            });
        });

        _provider = services.BuildServiceProvider(validateScopes: true);
        _bus = _provider.GetRequiredService<IBusControl>();
        await _bus.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_bus is not null)
            await _bus.StopAsync();

        if (_provider is not null)
            await _provider.DisposeAsync();

        await _mongo.DisposeAsync();
        await _elasticsearch.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed class MessageProbeSink
{
    private readonly ConcurrentBag<object> _messages = [];

    public void Record<T>(T message) where T : class => _messages.Add(message);

    public int Count<T>(Func<T, bool>? predicate = null) where T : class =>
        _messages.OfType<T>().Count(message => predicate is null || predicate(message));
}

public sealed record InfrastructureProbeMessage(Guid Id, string Value);

public sealed class InfrastructureProbeConsumer(MessageProbeSink sink) : IConsumer<InfrastructureProbeMessage>
{
    public Task Consume(ConsumeContext<InfrastructureProbeMessage> context)
    {
        sink.Record(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class ConfirmSeatsProbeConsumer(MessageProbeSink sink) : IConsumer<ConfirmSeats>
{
    public Task Consume(ConsumeContext<ConfirmSeats> context)
    {
        sink.Record(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class ProcessPaymentProbeConsumer(MessageProbeSink sink) : IConsumer<ProcessPayment>
{
    public Task Consume(ConsumeContext<ProcessPayment> context)
    {
        sink.Record(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class IssueTicketsProbeConsumer(MessageProbeSink sink) : IConsumer<IssueTickets>
{
    public Task Consume(ConsumeContext<IssueTickets> context)
    {
        sink.Record(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class OrderCompletedProbeConsumer(MessageProbeSink sink) : IConsumer<OrderCompleted>
{
    public Task Consume(ConsumeContext<OrderCompleted> context)
    {
        sink.Record(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class RecordingSaleRecordedPublisher : ISaleRecordedPublisher
{
    public ConcurrentBag<SaleRecorded> Published { get; } = [];

    public Task PublishAsync(SaleRecorded message, CancellationToken cancellationToken = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class MartenHarnessProbe
{
    public Guid Id { get; set; }
    public string Value { get; set; } = "";
}
