using TicketFlow.Payment.Application.Abstractions;
using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Phase12.HardeningTests.Support;

public enum ControlledSideEffectOutcome
{
    Succeed,
    FailBeforeSideEffect,
    SideEffectSucceededButResponseLost,
    Timeout,
}

public sealed class AmbiguousSideEffectException(string message) : Exception(message);

/// <summary>
/// Deterministic external-write probe shared by payment, object storage and email fakes.
/// Attempts and completed side effects are counted separately so a test can model the dangerous
/// case where the provider committed the write but the caller never received its response.
/// </summary>
public sealed class ControlledSideEffect<T>(
    Func<T> resultFactory,
    ControlledSideEffectOutcome outcome = ControlledSideEffectOutcome.Succeed,
    int rendezvousParticipants = 0)
{
    private readonly AsyncRendezvous? _rendezvous = rendezvousParticipants > 0
        ? new AsyncRendezvous(rendezvousParticipants)
        : null;
    private int _attempts;
    private int _successfulSideEffects;

    public int Attempts => Volatile.Read(ref _attempts);
    public int SuccessfulSideEffects => Volatile.Read(ref _successfulSideEffects);

    public async Task<T> InvokeAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _attempts);

        if (_rendezvous is not null)
            await _rendezvous.SignalAndWaitAsync(cancellationToken);

        switch (outcome)
        {
            case ControlledSideEffectOutcome.FailBeforeSideEffect:
                throw new InvalidOperationException("Controlled failure before external side effect.");
            case ControlledSideEffectOutcome.Timeout:
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable after an infinite delay.");
        }

        Interlocked.Increment(ref _successfulSideEffects);

        if (outcome == ControlledSideEffectOutcome.SideEffectSucceededButResponseLost)
            throw new AmbiguousSideEffectException("External side effect succeeded, but its response was lost.");

        return resultFactory();
    }

    public Task<bool> WaitForAttemptsAsync(int expected, TimeSpan timeout) =>
        AsyncTest.WaitUntilAsync(() => Attempts >= expected, timeout);

    public Task<bool> WaitForSuccessfulSideEffectsAsync(int expected, TimeSpan timeout) =>
        AsyncTest.WaitUntilAsync(() => SuccessfulSideEffects >= expected, timeout);
}

public sealed class AsyncRendezvous(int participants)
{
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public Task SignalAndWaitAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _arrived) == participants)
            _allArrived.TrySetResult();

        return _allArrived.Task.WaitAsync(cancellationToken);
    }
}

public sealed class ControlledPaymentGatewayFake(ControlledSideEffect<GatewayChargeResult> sideEffect)
    : IPaymentGatewayClient
{
    public ControlledSideEffect<GatewayChargeResult> SideEffect { get; } = sideEffect;

    public Task<GatewayChargeResult> ChargeAsync(
        Guid orderId,
        decimal amount,
        string paymentMethod,
        CancellationToken cancellationToken = default) =>
        SideEffect.InvokeAsync(cancellationToken);
}

public sealed class ControlledTicketRendererFake(ControlledSideEffect<string> sideEffect)
    : ITicketPdfRenderer
{
    public ControlledSideEffect<string> SideEffect { get; } = sideEffect;

    public Task<string> RenderAndStoreAsync(
        TicketPdfModel model,
        string objectKey,
        CancellationToken cancellationToken = default) =>
        SideEffect.InvokeAsync(cancellationToken);
}

public sealed record ControlledEmail(string To, string Subject, string Body, string OperationKey);

/// <summary>
/// Notification has no delivery-provider port yet. This test-only fake defines the failure
/// semantics Commit 13 must preserve when that port is introduced.
/// </summary>
public sealed class ControlledEmailSenderFake(ControlledSideEffect<string> sideEffect)
{
    public ControlledSideEffect<string> SideEffect { get; } = sideEffect;

    public Task<string> SendAsync(ControlledEmail email, CancellationToken cancellationToken = default) =>
        SideEffect.InvokeAsync(cancellationToken);
}
