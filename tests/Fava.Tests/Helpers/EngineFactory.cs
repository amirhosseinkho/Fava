using Fava;
using Fava.Config;
using Microsoft.Extensions.Options;

namespace Fava.Tests.Helpers;

internal static class EngineFactory
{
    // deterministic jitter seed so retry delay assertions are stable across runs
    private const int TestJitterSeed = 42;

    public static DispatchDecisionEngine Create(Action<RetryPolicyOptions>? configure = null)
    {
        var opts = new RetryPolicyOptions();
        configure?.Invoke(opts);
        return new DispatchDecisionEngine(Options.Create(opts), seed: TestJitterSeed);
    }
}
