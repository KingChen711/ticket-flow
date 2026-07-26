using Amazon.Lambda.Core;
using System.Text.Json;
using TicketFlow.Functions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

// Test-only implementation of the one AWS Lambda Invoke API operation Ticketing consumes. It
// deliberately executes the production RenderTicketPdfFunction, including QuestPDF, QRCoder and
// the MinIO write. CI therefore substitutes only LocalStack's control plane, not the PDF business
// behavior or object-storage side effect being verified by the E2E suite.
app.MapPost("/2015-03-31/functions/{functionName}/invocations", async (
    string functionName,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(functionName, "RenderTicketPdfFunction", StringComparison.Ordinal))
        return Results.NotFound(new { errorMessage = $"Unknown test function '{functionName}'." });

    try
    {
        // AWSSDK.Lambda sends Invoke payloads as application/octet-stream even though the bytes
        // contain JSON. Minimal API model binding rejects that media type with 415, so consume the
        // Lambda wire payload directly just like the real control plane does.
        var request = await JsonSerializer.DeserializeAsync<RenderTicketPdfRequest>(
            httpContext.Request.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken) ?? throw new InvalidOperationException("Lambda invoke payload was empty.");
        var response = await new RenderTicketPdfFunction().Handler(
            request,
            new E2ELambdaContext(cancellationToken));
        return Results.Json(response);
    }
    catch (Exception exception)
    {
        // Lambda's RequestResponse invoke returns a 200 envelope and marks function exceptions in
        // a response header. AWSSDK.Lambda turns that into FunctionError for the production client.
        httpContext.Response.Headers["X-Amz-Function-Error"] = "Unhandled";
        return Results.Json(
            new { errorMessage = exception.Message, errorType = exception.GetType().FullName },
            statusCode: StatusCodes.Status200OK);
    }
});

app.Run();

internal sealed class E2ELambdaContext(CancellationToken cancellationToken) : ILambdaContext
{
    public string AwsRequestId { get; } = Guid.NewGuid().ToString("N");
    public IClientContext ClientContext => null!;
    public string FunctionName => "render-ticket-pdf";
    public string FunctionVersion => "$LATEST";
    public ICognitoIdentity Identity => null!;
    public string InvokedFunctionArn => "arn:aws:lambda:local:000000000000:function:render-ticket-pdf";
    public ILambdaLogger Logger { get; } = new E2ELambdaLogger();
    public string LogGroupName => "/aws/lambda/render-ticket-pdf";
    public string LogStreamName => "e2e";
    public int MemoryLimitInMB => 512;
    public TimeSpan RemainingTime => cancellationToken.IsCancellationRequested
        ? TimeSpan.Zero
        : TimeSpan.FromMinutes(1);
}

internal sealed class E2ELambdaLogger : ILambdaLogger
{
    public void Log(string message) => Console.Write(message);

    public void LogLine(string message) => Console.WriteLine(message);
}
