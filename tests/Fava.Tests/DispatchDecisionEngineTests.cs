using Fava.Config;
using Fava.Models;
using Fava.Tests.Helpers;

namespace Fava.Tests;

public class DispatchDecisionEngineTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);

    // --- channel health ---

    [Fact]
    public void Bound_healthy_channel_submits_now()
    {
        var engine = EngineFactory.Create();
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.Healthy(), retryCount: 0, Now);

        Assert.Equal(DispatchAction.SubmitNow, result.Action);
        Assert.Equal("CHANNEL_HEALTHY", result.ReasonCode);
        Assert.False(result.PauseConsumer);
        Assert.False(result.AckOriginalMessage);
    }

    [Fact]
    public void Unbound_channel_pauses_consumer_and_does_not_submit()
    {
        var engine = EngineFactory.Create();
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithStatus(ProviderStatus.Unbound), retryCount: 0, Now);

        Assert.Equal(DispatchAction.PauseChannel, result.Action);
        Assert.Equal("CHANNEL_UNBOUND", result.ReasonCode);
        Assert.True(result.PauseConsumer);
        Assert.False(result.PublishRetryMessage);
    }

    [Fact]
    public void Closed_channel_does_not_immediate_requeue()
    {
        var engine = EngineFactory.Create();
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithStatus(ProviderStatus.Closed), retryCount: 0, Now);

        // must be a delayed retry, never requeue-now
        Assert.Equal(DispatchAction.DelayedRetry, result.Action);
        Assert.NotNull(result.RetryDelay);
        Assert.True(result.RetryDelay!.Value.TotalMilliseconds > 0);
    }

    [Fact]
    public void Unavailable_provider_does_not_immediate_requeue()
    {
        var engine = EngineFactory.Create();
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithStatus(ProviderStatus.Unavailable), retryCount: 0, Now);

        Assert.Equal(DispatchAction.DelayedRetry, result.Action);
        Assert.NotNull(result.RetryDelay);
    }

    [Fact]
    public void Throttled_provider_schedules_delayed_retry()
    {
        var engine = EngineFactory.Create();
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithStatus(ProviderStatus.Throttled), retryCount: 0, Now);

        Assert.Equal(DispatchAction.DelayedRetry, result.Action);
        Assert.Equal("PROVIDER_THROTTLED", result.ReasonCode);
        Assert.NotNull(result.RetryDelay);
        Assert.True(result.IncrementRetryCount);
        Assert.True(result.PublishRetryMessage);
    }

    [Fact]
    public void QueueFull_delay_is_longer_than_throttled_delay()
    {
        var engine = EngineFactory.Create();
        var throttled = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithStatus(ProviderStatus.Throttled), retryCount: 0, Now);
        var queueFull = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithStatus(ProviderStatus.QueueFull), retryCount: 0, Now);

        Assert.True(queueFull.RetryDelay!.Value > throttled.RetryDelay!.Value,
            $"QueueFull delay ({queueFull.RetryDelay.Value.TotalMs()}) should exceed throttled ({throttled.RetryDelay.Value.TotalMs()})");
    }

    [Fact]
    public void PermanentFailure_goes_to_dead_letter()
    {
        var engine = EngineFactory.Create();
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithStatus(ProviderStatus.PermanentFailure), retryCount: 0, Now);

        Assert.Equal(DispatchAction.DeadLetter, result.Action);
        Assert.Equal("PROVIDER_PERMANENT_FAILURE", result.ReasonCode);
        Assert.True(result.PersistAudit);
        Assert.False(result.PublishRetryMessage);
    }

    [Fact]
    public void Retry_budget_exhausted_produces_dead_letter()
    {
        var engine = EngineFactory.Create(o => o.MaxRetryCount = 5);
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.Healthy(), retryCount: 5, Now);

        Assert.Equal(DispatchAction.DeadLetter, result.Action);
        Assert.Equal("RETRY_BUDGET_EXCEEDED", result.ReasonCode);
        Assert.True(result.PersistAudit);
    }

    [Fact]
    public void Cooldown_window_blocks_submission_until_expiry()
    {
        var engine = EngineFactory.Create();
        var channel = ChannelBuilder.WithCooldown(until: Now.AddMinutes(5));
        var result = engine.Decide(MessageBuilder.Any(), channel, retryCount: 0, Now);

        Assert.Equal(DispatchAction.DelayedRetry, result.Action);
        Assert.Equal("CHANNEL_IN_COOLDOWN", result.ReasonCode);
    }

    // --- TPS rules ---

    [Fact]
    public void Zero_active_replicas_fails_closed()
    {
        var engine = EngineFactory.Create();
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithReplicas(replicas: 0), retryCount: 0, Now);

        Assert.Equal(DispatchAction.Reject, result.Action);
        Assert.Equal("INVALID_REPLICA_COUNT", result.ReasonCode);
        Assert.True(result.PersistAudit);
    }

    [Fact]
    public void Zero_provider_cap_fails_closed()
    {
        var engine = EngineFactory.Create();
        var channel = ChannelBuilder.Healthy() with { ProviderGlobalTpsCap = 0 };
        var result = engine.Decide(MessageBuilder.Any(), channel, retryCount: 0, Now);

        Assert.Equal(DispatchAction.Reject, result.Action);
        Assert.Equal("INVALID_PROVIDER_CAP", result.ReasonCode);
    }

    [Fact]
    public void InFlight_at_pod_tps_cap_delays_rather_than_blindly_submitting()
    {
        // globalCap=100, replicas=4 -> podTps=25; in-flight=25 means we're already at the cap
        var engine = EngineFactory.Create();
        var channel = ChannelBuilder.WithReplicas(replicas: 4, globalCap: 100) with
        {
            Status = ProviderStatus.Bound,
            InFlightCount = 25
        };
        var result = engine.Decide(MessageBuilder.Any(), channel, retryCount: 0, Now);

        Assert.NotEqual(DispatchAction.SubmitNow, result.Action);
        Assert.Equal(DispatchAction.DelayedRetry, result.Action);
    }

    // --- determinism ---

    [Fact]
    public void Same_inputs_produce_identical_results_deterministic()
    {
        var engine = EngineFactory.Create(o => o.JitterMaxMs = 0);
        var channel = ChannelBuilder.WithStatus(ProviderStatus.Throttled);
        var msg = MessageBuilder.Any();

        var r1 = engine.Decide(msg, channel, retryCount: 1, Now);
        var r2 = engine.Decide(msg, channel, retryCount: 1, Now);

        Assert.Equal(r1.RetryDelay, r2.RetryDelay);
        Assert.Equal(r1.ReasonCode, r2.ReasonCode);
        Assert.Equal(r1.Action, r2.Action);
    }

    // --- output rule: every DeadLetter must PersistAudit ---

    [Theory]
    [InlineData(ProviderStatus.PermanentFailure)]
    public void DeadLetter_always_persists_audit(ProviderStatus status)
    {
        var engine = EngineFactory.Create();
        var result = engine.Decide(MessageBuilder.Any(), ChannelBuilder.WithStatus(status), retryCount: 0, Now);

        if (result.Action == DispatchAction.DeadLetter)
            Assert.True(result.PersistAudit);
    }

    // --- exponential backoff grows with retry count ---

    [Fact]
    public void Retry_delay_grows_with_retry_count()
    {
        var engine = EngineFactory.Create(o =>
        {
            o.JitterMaxMs = 0;
            o.ThrottledBaseDelayMs = 1000;
            o.BackoffMultiplier = 2.0;
        });
        var channel = ChannelBuilder.WithStatus(ProviderStatus.Throttled);

        var r0 = engine.Decide(MessageBuilder.Any(), channel, retryCount: 0, Now);
        var r1 = engine.Decide(MessageBuilder.Any(), channel, retryCount: 1, Now);
        var r2 = engine.Decide(MessageBuilder.Any(), channel, retryCount: 2, Now);

        Assert.True(r1.RetryDelay!.Value > r0.RetryDelay!.Value);
        Assert.True(r2.RetryDelay!.Value > r1.RetryDelay!.Value);
    }
}

internal static class TimeSpanExt
{
    public static double TotalMs(this TimeSpan ts) => ts.TotalMilliseconds;
}
