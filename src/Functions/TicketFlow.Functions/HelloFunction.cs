using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TicketFlow.Functions;

/// <summary>
/// Probe function (Phase 9 Commit 1) — confirms the whole chain works before any real function
/// depends on it: dotnet-lambda package → awslocal create-function --runtime dotnet10 →
/// LocalStack spawns the actual public.ecr.aws/lambda/dotnet:10 base image → invoke returns.
/// </summary>
public sealed class HelloFunction
{
    public string Handler(string input, ILambdaContext context)
    {
        context.Logger.LogInformation($"HelloFunction invoked with input: {input}");
        return $"Hello, {input}! (TicketFlow.Functions, request id {context.AwsRequestId})";
    }
}
