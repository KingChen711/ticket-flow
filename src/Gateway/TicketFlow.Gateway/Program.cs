using Amazon.Lambda;
using Amazon.Runtime;
using TicketFlow.Gateway;
using TicketFlow.Observability;
using TicketFlow.Web;

var builder = WebApplication.CreateBuilder(args);

// Phase 10 Commit 1 — gateway now goes through the same building block every other service
// uses (health check + exception middleware + observability). It never did before (gap since
// Phase 7, gateway wired auth by hand instead) — worth closing now specifically because gateway
// is the FIRST hop of every trace; leaving the one process at the front of the pipeline
// uninstrumented would have made "trace xuyên suốt" (this phase's DoD) impossible to satisfy.
builder.Services.AddTicketFlowWeb();
builder.Services.AddTicketFlowObservability(builder.Configuration, "gateway");
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
// "Failed to fetch" with no indication it was ever a CORS problem. SignalR still sends
// credentials by default even though this read-only public seat-status hub no longer receives a
// bearer token, so AllowCredentials requires an explicit origin allowlist rather than "*".
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:3000", "http://localhost:3001", "http://localhost:3002")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

// CORS must run before auth (doc comment above AddCors explains why), so it stays ahead of
// UseTicketFlowWeb() here — the one place gateway's pipeline order differs from every other
// service's. Trade-off: CORS middleware itself isn't wrapped by ExceptionHandlingMiddleware
// (which UseTicketFlowWeb registers second, not first, in this one process) — acceptable since
// CORS middleware doesn't throw in practice; every service downstream still gets the middleware
// AddTicketFlowWeb was already providing them (health check, exception handling, auth/authz).
app.UseCors();
app.UseTicketFlowWeb();
app.UseTicketFlowObservability();

app.MapGet("/", () => Results.Ok(new { message = "TicketFlow API Gateway" }));

app.MapMediaEndpoints();

app.MapReverseProxy();

app.Run();
