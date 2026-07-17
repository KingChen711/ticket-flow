using Amazon.Lambda.Core;

namespace TicketFlow.Functions;

/// <summary>
/// Probe function (Phase 9 Commit 1) — EventBridge schedule rule trigger, not HTTP invoke. Real
/// payload shape from EventBridge isn't needed for this probe (just proving the rule fires the
/// function on a cycle), so the input is accepted as an untyped JSON element rather than pulling
/// in Amazon.Lambda.CloudWatchEvents for a field this function never reads.
/// </summary>
public sealed class HelloTimerFunction
{
    public string Handler(System.Text.Json.JsonElement scheduledEvent, ILambdaContext context)
    {
        var message = $"HelloTimerFunction tick at {DateTimeOffset.UtcNow:O}, request id {context.AwsRequestId}";
        context.Logger.LogInformation(message);
        return message;
    }
}
