namespace TicketFlow.Messaging;

/// <summary>
/// Thrown by a consumer that cannot succeed no matter how many times the message is redelivered:
/// a malformed payload, an id that will never exist, a rule the message can never satisfy.
/// </summary>
/// <remarks>
/// Retrying a permanent fault is not merely wasteful — it delays the DLQ entry that an operator
/// needs in order to see the problem at all, and it keeps a poison message cycling through a queue
/// that healthy traffic shares.
/// </remarks>
public sealed class PermanentMessageFaultException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Thrown when a message arrived before the state it depends on. Nothing is wrong; it is simply
/// early, and it needs a longer, scheduled redelivery rather than a fast in-process retry.
/// </summary>
public sealed class OutOfOrderMessageException(string message)
    : Exception(message);

/// <summary>
/// One policy, three fault classes (Commit 9, replacing a flat 3×2s applied to every consumer of
/// every service).
/// </summary>
/// <remarks>
/// <para>
/// The old policy was wrong in both directions at once. A message that arrived out of order got
/// three attempts two seconds apart — the milestone it was waiting for could not plausibly arrive
/// in six seconds, so it went to <c>_error</c> and needed a manual redrive. Meanwhile a genuinely
/// permanent fault also got three attempts, burning retries on something that could never succeed.
/// </para>
/// <para>
/// Two tiers, deliberately different mechanisms. Immediate retry is in-process and holds the
/// consumer, so it stays short and is only for transient infrastructure blips. Delayed redelivery
/// puts the message back on the broker with a delay, releasing the consumer — which is what makes
/// minutes-long waits affordable for out-of-order messages.
/// </para>
/// </remarks>
public static class TicketFlowFaultPolicy
{
    /// <summary>Short in-process retries for transient infrastructure faults only.</summary>
    public static readonly TimeSpan[] ImmediateRetryIntervals =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
    ];

    /// <summary>
    /// Scheduled redelivery for messages that are merely early. The spread — seconds, then a
    /// minute, then five — is what gives an out-of-order milestone time to actually arrive.
    /// </summary>
    public static readonly TimeSpan[] DelayedRedeliveryIntervals =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];
}
