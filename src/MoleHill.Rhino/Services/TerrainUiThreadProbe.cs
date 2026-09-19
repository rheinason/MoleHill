using System.Diagnostics;

namespace MoleHill.Rhino.Services;

/// <summary>
/// How much of the UI thread MoleHill itself is using, so a wait on that thread can be attributed
/// rather than guessed at.
///
/// The edit-to-visible trace found a ~300 ms interval between a finished build posting its completion
/// callback and that callback running - 71-79% of every edit, and the largest term in the whole
/// workstream. Two fixes aimed at the message queue (Eto's invoke queue, then a 15 ms wake timer) failed
/// to move it, and the wake timer's own tick counter then showed **zero ticks delivered** during the
/// window while the timer was running. So the thread is not starved of messages; it is busy. This says
/// with what.
///
/// Counters only, accumulated across the process and read as deltas over an interval. They are cheap
/// enough to leave on: a stopwatch and an interlocked add per panel refresh.
/// </summary>
internal static class TerrainUiThreadProbe
{
    private static long _panelRefreshTicks;
    private static int _panelRefreshCount;

    /// <summary>Total stopwatch ticks spent inside the panel's refresh since the process started.</summary>
    public static long PanelRefreshTicks => Volatile.Read(ref _panelRefreshTicks);

    public static int PanelRefreshCount => Volatile.Read(ref _panelRefreshCount);

    public static void RecordPanelRefresh(long elapsedTicks)
    {
        Interlocked.Add(ref _panelRefreshTicks, elapsedTicks);
        Interlocked.Increment(ref _panelRefreshCount);
    }

    public static double TicksToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;
}
