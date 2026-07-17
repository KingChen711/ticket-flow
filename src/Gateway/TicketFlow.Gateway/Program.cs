using Amazon.Lambda;
using Amazon.Runtime;
using TicketFlow.Gateway;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTicketFlowAuth(builder.Configuration);
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Phase 9 Commit 2 — media Lambdas are invoked via the Lambda Invoke API (MediaEndpoints.cs),
// not proxied through YARP; see that file's doc comment for why Function URLs didn't work out.
builder.Services.AddSingleton<IAmazonLambda>(_ =>
{
    var serviceUrl = builder.Configuration["Aws:LambdaServiceUrl"]
        ?? throw new InvalidOperationException("Configuration 'Aws:LambdaServiceUrl' was not found.");
    // LocalStack doesn't validate credentials, but the SDK still needs non-empty values to sign
    // requests — same "test"/"test" convention already used for this dev environment's own
    // ~/.aws/credentials (docs/phase-9-plan.md Commit 1).
    return new AmazonLambdaClient(new BasicAWSCredentials("test", "test"), new AmazonLambdaConfig { ServiceURL = serviceUrl });
});

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

app.MapMediaEndpoints();

app.MapReverseProxy();

app.Run();
