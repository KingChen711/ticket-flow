using Microsoft.AspNetCore.Server.Kestrel.Core;
using TicketFlow.Reservation.Api.Services;
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

var app = builder.Build();

app.UseTicketFlowWeb();

app.MapGrpcService<ReservationServiceImpl>();

app.Run();
