using MassTransit;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Contracts.Messaging;
using TicketFlow.Messaging;
using TicketFlow.Observability;
using TicketFlow.Reservation.Api.Consumers;
using TicketFlow.Reservation.Api.Endpoints;
using TicketFlow.Reservation.Api.Hubs;
using TicketFlow.Reservation.Api.Messaging;
using TicketFlow.Reservation.Api.Services;
using TicketFlow.Reservation.Application;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Infrastructure;
using TicketFlow.Reservation.Infrastructure.Reconciliation;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

if (EfMigrationRunner.IsRequested(args))
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' was not found.");
    await using var dbContext = ReservationDbContextFactory.Create(connectionString);
    await EfMigrationRunner.RunAsync(dbContext, "reservation");
    return;
}

var httpPort = builder.Configuration.GetValue("Kestrel:HttpPort", 8003);
var grpcPort = builder.Configuration.GetValue("Kestrel:GrpcPort", 9003);
var reconcileModeName = builder.Configuration["Reservation:CacheReconcileMode"] ?? "RepairCache";
if (!Enum.TryParse<SeatCacheReconcileMode>(reconcileModeName, ignoreCase: true, out var reconcileMode))
{
    throw new InvalidOperationException(
        $"Reservation:CacheReconcileMode '{reconcileModeName}' is invalid. " +
        $"Expected one of: {string.Join(", ", Enum.GetNames<SeatCacheReconcileMode>())}.");
}

// gRPC needs HTTP/2; REST (health check) needs HTTP/1.1. Plaintext (no TLS) Kestrel can't
// auto-negotiate between the two on one port the way it does over TLS ALPN, so they get
// separate ports — matches the 8003/9003 split in scope.md's service table.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(grpcPort, o => o.Protocols = HttpProtocols.Http2);
});

builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(
    builder.Configuration,
    "reservation",
    configureMetrics: m => m
        .AddMeter(ContractVersionMetrics.MeterName)
        // Commit 5: stale compensations and per-user hold rejections are silent no-ops by design,
        // so they only exist operationally if they are counted.
        .AddMeter(ReservationCompensationMetrics.MeterName));
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddGrpc();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ISeatMapNotifier, SignalRSeatMapNotifier>();
builder.Services.AddScoped<ISeatAvailabilityEventPublisher, MassTransitSeatAvailabilityEventPublisher>();
builder.Services.AddHostedService<ReservationCacheReconciliationWorker>();

// Postgres + Redis wiring (Infrastructure), then the orchestration services on top
// (Application) — ISeatHoldStore now has a real implementation (Commit 4-6), so this no
// longer trips ASP.NET Core's DI validation at Build() the way it would have in Commit 3/4.
builder.Services.AddReservationInfrastructure(builder.Configuration);
builder.Services.AddDbContextReadinessCheck<ReservationDbContext>("postgres");
builder.Services.AddReservationApplication();

builder.Services.AddTicketFlowMessaging(builder.Configuration, "reservation", cfg =>
{
    cfg.AddEntityFrameworkOutbox<ReservationDbContext>(outbox =>
    {
        outbox.UsePostgres();
        outbox.UseBusOutbox();
    });

    cfg.AddConsumer<ConfirmSeatsConsumer>();
    cfg.AddConsumer<ReleaseSeatsConsumer>();
    cfg.AddConsumer<CatalogSalePolicyChangedConsumer>();
    cfg.AddConsumer<SeatMapNotificationConsumer>();
}, configureRabbitMq: (context, rabbit) =>
{
    // These names match the queues previously created by ConfigureEndpoints, so enabling the
    // EF consumer outbox continues the existing endpoints instead of creating replacement queues.
    rabbit.ReceiveEndpoint("reservation-confirm-seats", endpoint =>
    {
        endpoint.SetQuorumQueue();
        endpoint.ConcurrentMessageLimit = 8;
        endpoint.UseEntityFrameworkOutbox<ReservationDbContext>(context);
        endpoint.ConfigureConsumer<ConfirmSeatsConsumer>(context);
    });

    rabbit.ReceiveEndpoint("reservation-release-seats", endpoint =>
    {
        endpoint.SetQuorumQueue();
        endpoint.ConcurrentMessageLimit = 8;
        endpoint.UseEntityFrameworkOutbox<ReservationDbContext>(context);
        endpoint.ConfigureConsumer<ReleaseSeatsConsumer>(context);
    });

    rabbit.ReceiveEndpoint("reservation-catalog-sale-policy-changed", endpoint =>
    {
        endpoint.SetQuorumQueue();
        // Serialize snapshots so two first deliveries cannot race the EventId primary key.
        endpoint.ConcurrentMessageLimit = 1;
        endpoint.UseEntityFrameworkOutbox<ReservationDbContext>(context);
        endpoint.ConfigureConsumer<CatalogSalePolicyChangedConsumer>(context);
    });

    // SignalR is a retryable projection on a separate queue. It intentionally has no EF outbox:
    // hub failure must never roll back or redeliver the allocation transaction that emitted the
    // SeatAvailabilityChanged event.
    rabbit.ReceiveEndpoint("reservation-seat-map-notification", endpoint =>
    {
        endpoint.SetQuorumQueue();
        // Held and Sold for one quote intentionally share its allocation generation. Serialize
        // the projection queue so a faster Sold hub call cannot be overtaken by the earlier Held
        // notification for that same seat.
        endpoint.ConcurrentMessageLimit = 1;
        endpoint.ConfigureConsumer<SeatMapNotificationConsumer>(context);
    });
});

var app = builder.Build();

// Dev-only: auto-migrate on startup. A real deployment would run migrations as a separate
// CI/CD step instead of on every app boot (avoids races across multiple instances).
if (!app.Environment.IsProduction() &&
    !string.Equals(builder.Configuration["Database:MigrateOnStartup"], "false", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ReservationDbContext>();
    await dbContext.Database.MigrateAsync();
}

// PostgreSQL is the allocation authority. Runtime behavior follows the configured mode: Report
// is observational, RepairCache fails closed on ownership conflict, and QuarantineConflict
// explicitly contains conflicts while repairing safe drift.
using (var scope = app.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<ISeatCacheBootstrapper>();
    var results = await bootstrapper.ReconcileAllAsync(reconcileMode);
    var unresolved = results
        .Select(result => new
        {
            Result = result,
            Drifts = result.RemainingDrifts
                .Where(drift =>
                    reconcileMode != SeatCacheReconcileMode.QuarantineConflict ||
                    !result.QuarantinedSeatIds.Contains(drift.SeatId))
                .ToArray(),
        })
        .Where(item => item.Drifts.Length > 0)
        .ToArray();
    if (unresolved.Length > 0 && reconcileMode == SeatCacheReconcileMode.Report)
    {
        app.Logger.LogWarning(
            "Reservation Redis bootstrap report found {DriftCount} drift(s) across {EventCount} event(s); Report mode is read-only and startup will continue.",
            unresolved.Sum(item => item.Drifts.Length),
            unresolved.Length);
    }
    else if (unresolved.Length > 0)
    {
        var summary = string.Join(
            "; ",
            unresolved.Select(item =>
                $"event {item.Result.EventId}: {item.Drifts.Length} unresolved drift(s) " +
                $"[{string.Join(", ", item.Drifts.Take(5).Select(drift => $"{drift.SeatId}/{drift.Kind}"))}]"));
        throw new InvalidOperationException(
            $"Reservation Redis bootstrap ({reconcileMode}) failed closed: {summary}");
    }
}

app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapInventoryEndpoints();
app.MapAvailabilityEndpoints();
app.MapHoldEndpoints();

// Anonymous at the service tier, same as the REST endpoints above (Phase 7 Commit 4 mục 3 —
// gateway is the trust boundary for this route, reservation itself stays a trusted-network
// callee for S2S). Path under /hubs so AddTicketFlowAuth's query-string-token fallback (gateway
// side) can key off a stable prefix if more hubs ever get added.
app.MapHub<SeatMapHub>("/hubs/seatmap");

app.MapGrpcService<ReservationServiceImpl>();

app.Run();
