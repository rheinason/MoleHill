using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Pins the hosting Rhino to the performance cores of a hybrid CPU, and raises its priority, before anything is
/// timed. <see cref="HostedPowerThrottling"/> alone was not enough: with throttling off, identical code still
/// measured in two states about 2x apart on the maintainer's i7-13700K (Grade Pad 76 ms or 158 ms three
/// minutes apart, 2026-10-07; the whole geometry-heavy scenario "regressed" on 2026-11-23 on main and the branch
/// alike). The slot is never the foreground window, so Windows may still place its threads on efficiency cores.
/// Restricting the process to the highest efficiency class removes that choice. The change is per process, so it
/// touches only the disposable slot.
/// </summary>
public static class HostedCorePinning
{
    private const int CpuSetInformation = 0;

    /// <summary>What was pinned, recorded with every hosted result; null until <see cref="Pin"/> has run.</summary>
    public static string? Description { get; private set; }

    /// <summary>
    /// Restricts the current process to the logical processors of the highest efficiency class (the performance
    /// cores) and sets Above Normal priority. Idempotent. On a CPU whose cores are all one class nothing is pinned.
    /// </summary>
    public static string Pin()
    {
        if (Description != null)
            return Description;

        var process = Process.GetCurrentProcess();
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception)
        {
            // Priority is a help, not the guarantee; the affinity below is.
        }

        List<(int Index, byte EfficiencyClass)> processors = ReadProcessors();
        if (processors.Count == 0 || processors.Select(p => p.EfficiencyClass).Distinct().Count() < 2)
        {
            Description = $"not pinned: {Environment.ProcessorCount} logical processors of one core type";
            return Description;
        }

        byte fastest = processors.Max(p => p.EfficiencyClass);
        long mask = 0;
        foreach (var (index, efficiencyClass) in processors)
        {
            if (efficiencyClass == fastest && index < 64)
                mask |= 1L << index;
        }

        process.ProcessorAffinity = (IntPtr)mask;
        int count = processors.Count(p => p.EfficiencyClass == fastest);
        Description = $"pinned to {count} of {processors.Count} logical processors (performance cores, efficiency class {fastest})";
        return Description;
    }

    /// <summary>Each logical processor of group 0 with its efficiency class (higher is faster).</summary>
    private static List<(int Index, byte EfficiencyClass)> ReadProcessors()
    {
        var result = new List<(int, byte)>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return result;

        GetSystemCpuSetInformation(IntPtr.Zero, 0, out int needed, IntPtr.Zero, 0);
        if (needed <= 0)
            return result;

        IntPtr buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, needed, out needed, IntPtr.Zero, 0))
                return result;

            // SYSTEM_CPU_SET_INFORMATION: Size (4), Type (4), then for a CPU set: Id (4), Group (2),
            // LogicalProcessorIndex (1), CoreIndex (1), LastLevelCacheIndex (1), NumaNodeIndex (1),
            // EfficiencyClass (1), ... Entries are walked by their own Size so a longer future layout still works.
            int offset = 0;
            while (offset + 19 <= needed)
            {
                int size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0)
                    break;

                int type = Marshal.ReadInt32(buffer, offset + 4);
                short group = Marshal.ReadInt16(buffer, offset + 12);
                if (type == CpuSetInformation && group == 0)
                {
                    byte logical = Marshal.ReadByte(buffer, offset + 14);
                    byte efficiency = Marshal.ReadByte(buffer, offset + 18);
                    result.Add((logical, efficiency));
                }

                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, int bufferLength, out int returnedLength, IntPtr process, uint flags);
}
