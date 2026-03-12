using MoleHill.Core.Engine;
using Rhino.Geometry;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed record TerrainBuildTiming(string Stage, TimeSpan Elapsed, string? Detail);

internal sealed class TerrainBuildResult
{
    public Mesh? PrimaryMesh { get; set; }

    public List<GeneratedRhinoObject> ZoneObjects { get; } = new();

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; } = new();

    public List<string> Diagnostics { get; } = new();

    public TerrainAnalysisSummary? Analysis { get; set; }

    public List<TerrainBuildTiming> Timings { get; } = new();

    public List<SurfaceRemesher.ConstraintPolyline> PersistentHardConstraints { get; } = new();

    public void RecordTiming(string stage, TimeSpan elapsed, string? detail = null, int diagnosticThresholdMs = int.MaxValue)
    {
        Timings.Add(new TerrainBuildTiming(stage, elapsed, detail));
        if (elapsed.TotalMilliseconds < diagnosticThresholdMs)
            return;

        Diagnostics.Add(FormatTimingMessage(stage, elapsed, detail));
    }

    private static string FormatTimingMessage(string stage, TimeSpan elapsed, string? detail)
    {
        string message = $"Timing: {stage} in {elapsed.TotalSeconds:0.##} s";
        return string.IsNullOrWhiteSpace(detail)
            ? message + "."
            : $"{message} ({detail}).";
    }
}
