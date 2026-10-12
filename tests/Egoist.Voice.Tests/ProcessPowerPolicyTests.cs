using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class ProcessPowerPolicyTests
{
    [Fact]
    public void Disables_execution_speed_throttling_with_the_documented_structure()
    {
        int? cls = null, size = null; ProcessPowerPolicy.PowerThrottlingState captured = default;
        var applied = ProcessPowerPolicy.DisableThrottling((int c, ref ProcessPowerPolicy.PowerThrottlingState s, int n) =>
        { cls = c; size = n; captured = s; return true; });
        Assert.True(applied);
        Assert.Equal(4, cls);          // ProcessPowerThrottling
        Assert.Equal(12, size);        // three UINT fields
        Assert.Equal(1u, captured.Version);
        Assert.Equal(1u, captured.ControlMask);   // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
        Assert.Equal(0u, captured.StateMask);     // 0 = throttling off
    }

    [Fact]
    public void Failure_or_exception_from_the_system_call_is_swallowed()
    {
        Assert.False(ProcessPowerPolicy.DisableThrottling((int c, ref ProcessPowerPolicy.PowerThrottlingState s, int n) => false));
        Assert.False(ProcessPowerPolicy.DisableThrottling((int c, ref ProcessPowerPolicy.PowerThrottlingState s, int n) =>
            throw new EntryPointNotFoundException()));
    }
}
