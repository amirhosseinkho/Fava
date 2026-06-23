using Fava.Models;

namespace Fava.Tests.Helpers;

internal static class ChannelBuilder
{
    public static ChannelRuntimeState Healthy(int channelId = 1) =>
        new(
            ChannelId: channelId,
            Status: ProviderStatus.Bound,
            ProviderGlobalTpsCap: 1000,
            ActiveReplicas: 4,
            InFlightCount: 0,
            QueueReadyCount: 0,
            QueueUnackedCount: 0,
            CooldownUntilUtc: null,
            LastStateChangeUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

    public static ChannelRuntimeState WithStatus(ProviderStatus status, int channelId = 1) =>
        Healthy(channelId) with { Status = status };

    public static ChannelRuntimeState WithCooldown(DateTimeOffset until) =>
        Healthy() with { CooldownUntilUtc = until };

    public static ChannelRuntimeState WithReplicas(int replicas, int globalCap = 1000) =>
        Healthy() with { ActiveReplicas = replicas, ProviderGlobalTpsCap = globalCap };

    public static ChannelRuntimeState WithInFlight(int count) =>
        Healthy() with { InFlightCount = count };
}
