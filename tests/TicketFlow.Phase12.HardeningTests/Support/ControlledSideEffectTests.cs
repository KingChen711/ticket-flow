namespace TicketFlow.Phase12.HardeningTests.Support;

public sealed class ControlledSideEffectTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Barrier_releases_concurrent_attempts_and_counts_each_external_write()
    {
        var effect = new ControlledSideEffect<string>(() => "ok", rendezvousParticipants: 2);

        var results = await Task.WhenAll(effect.InvokeAsync(), effect.InvokeAsync());

        Assert.Equal(["ok", "ok"], results);
        Assert.Equal(2, effect.Attempts);
        Assert.Equal(2, effect.SuccessfulSideEffects);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Ambiguous_outcome_counts_the_side_effect_even_when_the_response_is_lost()
    {
        var effect = new ControlledSideEffect<string>(
            () => "provider-reference",
            ControlledSideEffectOutcome.SideEffectSucceededButResponseLost);

        await Assert.ThrowsAsync<AmbiguousSideEffectException>(() => effect.InvokeAsync());

        Assert.Equal(1, effect.Attempts);
        Assert.Equal(1, effect.SuccessfulSideEffects);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Timeout_mode_is_bounded_by_the_callers_cancellation_token()
    {
        var effect = new ControlledSideEffect<string>(() => "never", ControlledSideEffectOutcome.Timeout);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => effect.InvokeAsync(timeout.Token));

        Assert.Equal(1, effect.Attempts);
        Assert.Equal(0, effect.SuccessfulSideEffects);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Email_fake_has_the_same_operation_key_and_ambiguous_result_semantics()
    {
        var effect = new ControlledSideEffect<string>(
            () => "mail-provider-reference",
            ControlledSideEffectOutcome.SideEffectSucceededButResponseLost);
        var sender = new ControlledEmailSenderFake(effect);

        await Assert.ThrowsAsync<AmbiguousSideEffectException>(() => sender.SendAsync(
            new ControlledEmail("buyer@example.com", "Ticket ready", "...", "order-123:ready")));

        Assert.Equal(1, sender.SideEffect.SuccessfulSideEffects);
    }
}
