using System.Runtime.InteropServices;

namespace Egoist.Voice.Services;

/// <summary>
/// Opts the process out of power throttling (EcoQoS). Windows 11 may otherwise demote a
/// background tray process to efficiency cores at low clocks, which makes the first dictation
/// after idle noticeably slower.
/// </summary>
internal static class ProcessPowerPolicy
{
    internal const int ProcessPowerThrottlingClass = 4;
    internal const uint CurrentVersion = 1;
    internal const uint ExecutionSpeed = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>Control mask set, state mask zero: the system must not throttle execution speed.</summary>
    internal static PowerThrottlingState CreateDisabledThrottlingState() => new()
    {
        Version = CurrentVersion,
        ControlMask = ExecutionSpeed,
        StateMask = 0
    };

    internal delegate bool SetInformation(int informationClass, ref PowerThrottlingState state, int size);

    /// <summary>Never throws: older Windows simply lacks the class and keeps default behaviour.</summary>
    internal static bool DisableThrottling(SetInformation? setter = null)
    {
        try
        {
            var state = CreateDisabledThrottlingState();
            var applied = (setter ?? SetForCurrentProcess)(
                ProcessPowerThrottlingClass, ref state, Marshal.SizeOf<PowerThrottlingState>());
            if (!applied)
                AppLog.Write($"Power throttling opt-out was not applied: win32={Marshal.GetLastWin32Error()}");
            return applied;
        }
        catch (Exception exception)
        {
            AppLog.Write("Power throttling opt-out failed", exception);
            return false;
        }
    }

    private static bool SetForCurrentProcess(int informationClass, ref PowerThrottlingState state, int size) =>
        SetProcessInformation(GetCurrentProcess(), informationClass, ref state, size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process, int informationClass, ref PowerThrottlingState information, int size);
}
