using System.Collections.Concurrent;
using System.Diagnostics;

namespace MoleHill.Rhino.Services;

/// <summary>
/// One timestamped point on a rebuild request's path from the edit that caused it to the frame that
/// shows it. Timestamps are monotonic <see cref="Stopwatch"/> ticks, so they are comparable across the
/// UI thread and the build worker; wall-clock is deliberately not used.
/// </summary>
internal sealed record TerrainLatencyEvent(
    long Timestamp,
    uint DocSerial,
    Guid TerrainId,
    long Version,
    long Generation,
    TerrainBuildMode Mode,
    string Phase,
    string? Detail);

/// <summary>
/// The phase vocabulary. Consecutive events partition a request's lifetime completely, so the report
/// can account for every interval instead of summing selected stage timers - the failing of
/// <c>Rebuild total</c>, which starts at the snapshot and so cannot see queue or dispatch delay.
/// </summary>
internal static class TerrainLatencyPhase
{
    public const string Edit = "edit";
    public const string Due = "due";
    public const string DispatchBlocked = "dispatch-blocked";
    public const string Dispatch = "dispatch";
    public const string CancelRequested = "cancel-requested";
    public const string SnapshotStart = "snapshot-start";
    public const string SnapshotEnd = "snapshot-end";
    public const string CloneEnd = "clone-end";
    public const string WorkerQueued = "worker-queued";
    public const string WorkerStart = "worker-start";
    public const string GeometryReady = "geometry-ready";
    public const string OutputsEnd = "outputs-end";
    public const string WorkerEnd = "worker-end";
    public const string WakePosted = "wake-posted";
    public const string WakeRan = "wake-ran";
    public const string CompletionDispatch = "completion-dispatch";
    public const string MergeEnd = "merge-end";
    public const string DisplayEnd = "display-end";
    public const string SyncEnd = "sync-end";
    public const string SaveEnd = "save-end";
    public const string RedrawEnd = "redraw-end";
    public const string Closed = "closed";

    /// <summary>Per-family marks inside the final-only output block, e.g. <c>outputs:zones</c>.</summary>
    public const string OutputFamilyPrefix = "outputs:";
}

/// <summary>
/// Process-wide ring buffer of latency events. Off by default and cheap when off: every recording site
/// reads one volatile bool first. Enabled by the <c>mhLatencyTrace</c> command or by setting
/// <c>MOLEHILL_LATENCY_TRACE=1</c> before Rhino starts.
/// </summary>
internal static class TerrainLatencyTrace
{
    private const int Capacity = 20_000;

    private static readonly ConcurrentQueue<TerrainLatencyEvent> Events = new();
    private static volatile bool _enabled =
        System.Environment.GetEnvironmentVariable("MOLEHILL_LATENCY_TRACE") is "1" or "true" or "TRUE";

    public static bool IsEnabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static long Now => Stopwatch.GetTimestamp();

    public static double MillisecondsBetween(long from, long to) =>
        (to - from) * 1000.0 / Stopwatch.Frequency;

    public static void Record(
        uint docSerial,
        Guid terrainId,
        long version,
        long generation,
        TerrainBuildMode mode,
        string phase,
        string? detail = null)
    {
        if (!_enabled)
            return;

        Events.Enqueue(new TerrainLatencyEvent(Stopwatch.GetTimestamp(), docSerial, terrainId, version, generation, mode, phase, detail));
        while (Events.Count > Capacity && Events.TryDequeue(out _))
        {
        }
    }

    public static IReadOnlyList<TerrainLatencyEvent> Snapshot() => Events.ToArray();

    public static void Clear()
    {
        while (Events.TryDequeue(out _))
        {
        }
    }
}

/// <summary>
/// Identity for one rebuild request, so the worker can mark phases without re-threading four arguments
/// through the build service. A scope is created per request and carried into
/// <see cref="TerrainBuildService.Build(TerrainBuildSnapshot, TerrainRuntimeCache, TerrainBuildMode, Func{bool}?, Action{TerrainBuildProgress}?, TerrainLatencyScope?)"/>.
/// </summary>
internal sealed class TerrainLatencyScope
{
    public TerrainLatencyScope(uint docSerial, Guid terrainId, long version, long generation, TerrainBuildMode mode)
    {
        DocSerial = docSerial;
        TerrainId = terrainId;
        Version = version;
        Generation = generation;
        Mode = mode;
    }

    public uint DocSerial { get; }

    public Guid TerrainId { get; }

    public long Version { get; }

    public long Generation { get; }

    public TerrainBuildMode Mode { get; }

    public void Mark(string phase, string? detail = null) =>
        TerrainLatencyTrace.Record(DocSerial, TerrainId, Version, Generation, Mode, phase, detail);
}
