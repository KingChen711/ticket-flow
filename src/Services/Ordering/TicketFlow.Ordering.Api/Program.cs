using Scalar.AspNetCore;
using TicketFlow.Contracts.Reservation;
using TicketFlow.Ordering.Api.Endpoints;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddOpenApi();

builder.Services.AddGrpcClient<ReservationService.ReservationServiceClient>(options =>
{
    var address = builder.Configuration["Reservation:GrpcAddress"] ?? "http://localhost:9003";
    options.Address = new Uri(address);
});

var app = builder.Build();

app.UseTicketFlowWeb();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapCheckSeatsEndpoint();

app.Run();
