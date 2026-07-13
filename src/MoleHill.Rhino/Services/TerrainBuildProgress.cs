using System.Diagnostics;

namespace MoleHill.Rhino.Services;

/// <summary>Lightweight progress snapshots emitted by long-running terrain builds.</summary>
internal sealed record TerrainBuildProgress(
    string Stage,
    string State,
    TimeSpan TotalElapsed,
    TimeSpan StageElapsed,
    long ManagedBytes,
    string? Detail = null)
{
    public string Format()
    {
        string elapsed = StageElapsed > TimeSpan.Zero
            ? $"; stage {StageElapsed.TotalSeconds:0.###} s"
            : string.Empty;
        string detail = string.IsNullOrWhiteSpace(Detail) ? string.Empty : $"; {Detail}";
        return $"{Stage}: {State}{elapsed}; total {TotalElapsed.TotalSeconds:0.###} s; managed {ManagedBytes / (1024.0 * 1024.0):0.0} MB{detail}";
    }
}

internal sealed class TerrainBuildProgressReporter
{
    private readonly Action<TerrainBuildProgress>? _callback;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Dictionary<string, Stopwatch> _stages = new(StringComparer.Ordinal);

    public TerrainBuildProgressReporter(Action<TerrainBuildProgress>? callback)
    {
        _callback = callback;
    }

    public void Start(string stage, string? detail = null)
    {
        if (_callback == null)
            return;

        _stages[stage] = Stopwatch.StartNew();
        Publish(stage, "starting", TimeSpan.Zero, detail);
    }

    public void Complete(string stage, string? detail = null)
    {
        if (_callback == null)
            return;

        TimeSpan elapsed = _stages.Remove(stage, out Stopwatch? timer)
            ? timer.Elapsed
            : TimeSpan.Zero;
        Publish(stage, "complete", elapsed, detail);
    }

    public void Report(string stage, string state, string? detail = null)
    {
        if (_callback == null)
            return;

        TimeSpan elapsed = _stages.TryGetValue(stage, out Stopwatch? timer)
            ? timer.Elapsed
            : TimeSpan.Zero;
        Publish(stage, state, elapsed, detail);
    }

    private void Publish(string stage, string state, TimeSpan stageElapsed, string? detail)
    {
        _callback?.Invoke(new TerrainBuildProgress(
            stage,
            state,
            _total.Elapsed,
            stageElapsed,
            GC.GetTotalMemory(forceFullCollection: false),
            detail));
    }
}
