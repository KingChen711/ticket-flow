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
using TicketFlow.Reservation.Api.Services;
using TicketFlow.Reservation.Application;
using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

var httpPort = builder.Configuration.GetValue("Kestrel:HttpPort", 8003);
var grpcPort = builder.Configuration.GetValue("Kestrel:GrpcPort", 9003);

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
    configureMetrics: m => m.AddMeter(ContractVersionMetrics.MeterName));
builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddGrpc();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ISeatMapNotifier, SignalRSeatMapNotifier>();

// Postgres + Redis wiring (Infrastructure), then the orchestration services on top
// (Application) — ISeatHoldStore now has a real implementation (Commit 4-6), so this no
// longer trips ASP.NET Core's DI validation at Build() the way it would have in Commit 3/4.
builder.Services.AddReservationInfrastructure(builder.Configuration);
builder.Services.AddReservationApplication();

builder.Services.AddTicketFlowMessaging(builder.Configuration, "reservation", cfg =>
{
    cfg.AddConsumer<ConfirmSeatsConsumer>();
    cfg.AddConsumer<ReleaseSeatsConsumer>();
    cfg.AddConsumer<CatalogSalePolicyChangedConsumer>();
}, configureRabbitMq: (context, rabbit) =>
{
    rabbit.ReceiveEndpoint("reservation-catalog-sale-policy-changed", endpoint =>
    {
        // Serialize snapshots so two first deliveries cannot race the EventId primary key.
        endpoint.ConcurrentMessageLimit = 1;
        endpoint.ConfigureConsumer<CatalogSalePolicyChangedConsumer>(context);
    });
});

var app = builder.Build();

// Dev-only: auto-migrate on startup. A real deployment would run migrations as a separate
// CI/CD step instead of on every app boot (avoids races across multiple instances).
if (!app.Environment.IsProduction())
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ReservationDbContext>();
    await dbContext.Database.MigrateAsync();
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
