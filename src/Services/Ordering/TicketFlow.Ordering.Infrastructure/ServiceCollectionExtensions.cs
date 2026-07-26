using JasperFx;
using JasperFx.Events.Projections;
using Marten;
using Marten.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Checkout;
using TicketFlow.Ordering.Application.Sagas;
using TicketFlow.Ordering.Application.Workflows;
using TicketFlow.Ordering.Infrastructure.Messaging;

namespace TicketFlow.Ordering.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddMarten(options =>
        {
            options.Connection(connectionString);
            var allowRuntimeSchemaChanges = configuration.GetValue("Marten:AutoCreateSchemaObjects", true);
            Configure(options, allowRuntimeSchemaChanges ? AutoCreate.CreateOrUpdate : AutoCreate.None);
        });

        services.AddScoped<IOrderStore, MartenOrderStore>();
        services.AddScoped<ICheckoutOperationStore, MartenCheckoutOperationStore>();
        services.AddScoped<IOrderViewStore, MartenOrderViewStore>();
        services.AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();

        services.AddHealthChecks().AddCheck<MartenHealthCheck>(
            "marten", HealthStatus.Unhealthy, ["ready"], TimeSpan.FromSeconds(3));

        return services;
    }

    /// <summary>
    /// Everything about the store except which database it points at. Split out so the Phase 12
    /// hardening tests can prove these exact projections and indexes against real Postgres — a
    /// hand-copied test configuration would only prove the copy.
    /// </summary>
    public static void Configure(
        StoreOptions options,
        AutoCreate autoCreateSchemaObjects = AutoCreate.CreateOrUpdate)
    {
        // Production sets this to None; the image's explicit --migrate command is the sole
        // schema writer. Development keeps CreateOrUpdate for the existing fast local loop.
        options.AutoCreateSchemaObjects = autoCreateSchemaObjects;

        // Inline = read model updates in the SAME transaction as the event append, so a
        // query right after a command sees the new state immediately (read-after-write
        // consistency) — simplest option for Phase 4's DoD. Async/daemon projections
        // (eventual consistency, higher throughput) are a documented backlog item, not
        // needed here.
        options.Projections.Add<MyOrdersProjection>(ProjectionLifecycle.Inline);

        // Commit 7. The document id already makes (customer, idempotency key) unique; this
        // second index is what stops a client that simply sends a fresh key on every retry
        // from turning one seat hold into two orders. Postgres enforces it even when two
        // requests race past the service's own lookups.
        options.Schema.For<CheckoutOperation>().UniqueIndex(UniqueIndexType.Computed, x => x.HoldId);
        options.Schema.For<OrderDispatch>().Index(x => x.State);
        // The cancellation campaign queries purchase sagas by event, outside MassTransit's saga
        // repository. Declare the identity/index in Ordering's store configuration as well so
        // those queries do not depend on consumer-registration order to teach Marten the shape.
        options.Schema.For<PurchaseState>()
            .Identity(x => x.CorrelationId)
            .Index(x => x.CatalogEventId);
        options.Schema.For<EventCancellationCampaign>().UseOptimisticConcurrency(true);
        options.Schema.For<EventCancellationCampaign>().Index(x => x.EventId);
    }
}
