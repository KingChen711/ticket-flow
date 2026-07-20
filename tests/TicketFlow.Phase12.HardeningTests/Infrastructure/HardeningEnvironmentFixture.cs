using System.Collections.Concurrent;
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
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

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
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3-management").Build();

    private ServiceProvider? _provider;
    private IBusControl? _bus;

    public string PostgresConnectionString => _postgres.GetConnectionString();
    public string RedisConnectionString => _redis.GetConnectionString();
    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();
    public IDocumentStore MartenStore => _provider!.GetRequiredService<IDocumentStore>();
    public IPublishEndpoint PublishEndpoint => _bus!;
    public MessageProbeSink Messages => _provider!.GetRequiredService<MessageProbeSink>();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbitMq.StartAsync());

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

            configuration.AddSagaStateMachine<PurchaseSaga, PurchaseState>()
                .MartenRepository();

            configuration.AddConsumer<InfrastructureProbeConsumer>();
            configuration.AddConsumer<ConfirmSeatsProbeConsumer>();
            configuration.AddConsumer<ProcessPaymentProbeConsumer>();
            configuration.AddConsumer<IssueTicketsProbeConsumer>();
            configuration.AddConsumer<OrderCompletedProbeConsumer>();

            configuration.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(new Uri(RabbitMqConnectionString));
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
