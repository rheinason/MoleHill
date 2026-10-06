using System.Runtime.InteropServices;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Opts the hosting Rhino out of Windows power throttling (EcoQoS) before anything is timed.
///
/// A slot Rhino spawned for the hosted probes is visible but never the foreground window, so on a hybrid CPU
/// Windows treats it as background work and schedules its threads on the efficiency cores. Every metric then
/// reads about twice as slow, uniformly, and it looks exactly like a busy machine: on 2026-10-07 an idle
/// i7-13700K failed the lane on 43 metrics, and opting the same slot out of throttling brought it to 4. The
/// opt-out is per process, so it touches only the disposable slot.
/// </summary>
public static class HostedPowerThrottling
{
    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private const uint IgnoreTimerResolution = 0x4;

    /// <summary>Whether this process has opted out; recorded with every hosted result.</summary>
    public static bool IsOptedOut { get; private set; }

    /// <summary>Turns throttling off for the current process. Idempotent; returns whether it is off.</summary>
    public static bool OptOut()
    {
        if (IsOptedOut)
            return true;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
            return false;

        // A control bit with its state bit clear means "never throttle this", not "let the system decide".
        var state = new PowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ExecutionSpeed | IgnoreTimerResolution,
            StateMask = 0
        };
        IsOptedOut = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
        return IsOptedOut;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("kernel32")]
    private static extern IntPtr GetCurrentProcess();
}
