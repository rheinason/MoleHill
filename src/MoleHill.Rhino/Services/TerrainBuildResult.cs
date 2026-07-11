using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Rhino.Geometry;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed record TerrainBuildTiming(string Stage, TimeSpan Elapsed, string? Detail, bool IsCacheHit);

/// <summary>A short preview-only line segment drawn by the Retopo cross-field field preview (not baked).</summary>
internal readonly record struct FieldOverlayLine(Line Line, int Argb);

internal sealed class TerrainObjectPlacement
{
    public Guid ObjectId { get; init; }

    public Transform AppliedTransform { get; init; } = Transform.Identity;
}

internal sealed class TerrainObjectPlacementGroup
{
    public Guid DefinitionId { get; init; }

    public List<TerrainObjectPlacement> Placements { get; } = new();
}

internal sealed class TerrainBuildResult
{
    public TerrainBuildMode Mode { get; set; } = TerrainBuildMode.Final;

    public bool HasDeferredOutputs { get; set; }

    public Mesh? PrimaryMesh { get; set; }

    public Mesh? BaseMesh { get; set; }

    public List<GeneratedRhinoObject> ZoneObjects { get; } = new();

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; } = new();

    public List<GeneratedRhinoObject> ScatterObjects { get; } = new();

    public List<TerrainObjectPlacementGroup> ObjectPlacements { get; } = new();

    public List<string> Diagnostics { get; } = new();

    public List<GradingDiagnostic> StructuredDiagnostics { get; } = new();

    public List<TerrainAnalysisSummary> AnalysisResults { get; } = new();

    public List<TerrainBuildTiming> Timings { get; } = new();

    public List<SurfaceRemesher.ConstraintPolyline> PersistentHardConstraints { get; } = new();

    public List<SurfaceRemesher.ConstraintPolyline> PersistentElevationConstraints { get; } = new();

    /// <summary>Retopo Stage-1 cross-field preview overlay (short flow-direction segments; preview only, never baked).</summary>
    public List<FieldOverlayLine> FieldOverlayLines { get; } = new();

    public void RecordTiming(
        string stage,
        TimeSpan elapsed,
        string? detail = null,
        int diagnosticThresholdMs = int.MaxValue,
        bool isCacheHit = false)
    {
        bool resolvedCacheHit = isCacheHit ||
            detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true;
        Timings.Add(new TerrainBuildTiming(stage, elapsed, detail, resolvedCacheHit));
        if (elapsed.TotalMilliseconds < diagnosticThresholdMs)
            return;

        Diagnostics.Add(FormatTimingMessage(stage, elapsed, detail));
    }

    public void AddGradingDiagnostics(GradingResult result)
    {
        Diagnostics.AddRange(result.Diagnostics);
        StructuredDiagnostics.AddRange(result.StructuredDiagnostics);
    }

    private static string FormatTimingMessage(string stage, TimeSpan elapsed, string? detail)
    {
        string message = $"timing.{stage}: {elapsed.TotalSeconds:0.##} s";
        return string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message} | {detail}";
    }
}
