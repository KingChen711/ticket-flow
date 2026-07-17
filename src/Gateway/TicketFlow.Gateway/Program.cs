using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Phase 8 Commit 6 — the browser connects here directly for the SignalR seat map (the one
// exception to the BFF pattern, docs/phase-8-plan.md mục 2/5), so this is the one route real
// cross-origin browser requests hit. A real bug found via probing: the CORS *preflight* (OPTIONS)
// request carries no Authorization header by spec, and without CORS middleware ahead of auth,
// RequireAuthenticated 401s the preflight itself — the browser then reports a generic
// "Failed to fetch" with no indication it was ever a CORS problem. AllowCredentials required
// (SignalR negotiate sends the token), which means an explicit origin allowlist, not "*".
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:3000", "http://localhost:3001", "http://localhost:3002")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { message = "TicketFlow API Gateway" }));

app.MapReverseProxy();

app.Run();
