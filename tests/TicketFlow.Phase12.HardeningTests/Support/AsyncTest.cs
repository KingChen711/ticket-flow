namespace TicketFlow.Phase12.HardeningTests.Support;

internal static class AsyncTest
{
    public static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                return false;

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        return true;
    }
}
