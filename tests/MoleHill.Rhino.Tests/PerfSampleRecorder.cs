using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// One sample of a hosted-performance scenario: metric name to milliseconds. Metric names are
/// <c>{scenario}/{phase}/{stage}</c>, so a baseline can be compared scenario by scenario and a stage keeps
/// its identity across runs. A stage that reports twice in one build is summed, which is what its cost is.
/// </summary>
public sealed class PerfSampleRecorder
{
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The stage's own detail line (output counts, cache hits). Not compared - it is how a reader tells
    /// whether a changed time came with changed work, such as a fixture now producing more ponds.
    /// </summary>
    public Dictionary<string, string> Details { get; } = new(StringComparer.Ordinal);

    public void Record(string metric, double milliseconds)
    {
        Values[metric] = Values.TryGetValue(metric, out double existing)
            ? existing + milliseconds
            : milliseconds;
    }

    /// <summary>
    /// The wall time the caller measured around <see cref="TerrainBuildService.Build"/> plus every stage row
    /// the build reported, including per-analysis rows such as <c>Analysis Ponding</c>.
    /// </summary>
    internal void RecordBuild(string prefix, TerrainBuildResult result, TimeSpan wall)
    {
        Record($"{prefix}/wall", wall.TotalMilliseconds);
        foreach (TerrainBuildTiming timing in result.Timings)
        {
            Record($"{prefix}/{timing.Stage}", timing.Elapsed.TotalMilliseconds);
            if (!string.IsNullOrEmpty(timing.Detail))
                Details[$"{prefix}/{timing.Stage}"] = timing.Detail;
        }
    }
}
