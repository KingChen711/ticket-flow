using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowWeb();
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

app.MapGet("/hello", () => Results.Ok(new { message = "hello from catalog-service" }))
   .WithName("Hello");

app.Run();
