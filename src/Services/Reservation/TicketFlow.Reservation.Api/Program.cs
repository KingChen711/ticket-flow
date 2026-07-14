using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TicketFlow.Messaging;
using TicketFlow.Reservation.Api.Consumers;
using TicketFlow.Reservation.Api.Endpoints;
using TicketFlow.Reservation.Api.Services;
using TicketFlow.Reservation.Application;
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
builder.Services.AddGrpc();
builder.Services.AddOpenApi();

// Postgres + Redis wiring (Infrastructure), then the orchestration services on top
// (Application) — ISeatHoldStore now has a real implementation (Commit 4-6), so this no
// longer trips ASP.NET Core's DI validation at Build() the way it would have in Commit 3/4.
builder.Services.AddReservationInfrastructure(builder.Configuration);
builder.Services.AddReservationApplication();

builder.Services.AddTicketFlowMessaging(builder.Configuration, "reservation", cfg =>
{
    cfg.AddConsumer<ConfirmSeatsConsumer>();
    cfg.AddConsumer<ReleaseSeatsConsumer>();
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

app.MapOpenApi();
app.MapScalarApiReference();

app.MapInventoryEndpoints();
app.MapAvailabilityEndpoints();
app.MapHoldEndpoints();

app.MapGrpcService<ReservationServiceImpl>();

app.Run();
