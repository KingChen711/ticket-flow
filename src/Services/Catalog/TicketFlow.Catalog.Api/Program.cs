using TicketFlow.Catalog.Api.Endpoints;
using TicketFlow.Catalog.Application;
using TicketFlow.Catalog.Infrastructure;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
builder.Services.AddCatalogApplication();
builder.Services.AddCatalogInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "TicketFlow Catalog API",
        Version = "v1"
    });
});

var app = builder.Build();

app.UseTicketFlowWeb();

app.UseSwagger();
app.UseSwaggerUI();

app.MapVenueEndpoints();
app.MapEventEndpoints();
app.MapSeatMapEndpoints();

app.Run();
